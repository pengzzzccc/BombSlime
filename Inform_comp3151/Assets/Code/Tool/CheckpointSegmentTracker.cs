using System;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Inkform.Tool
{
    /// <summary>
    /// The perf log's gameplay half: turns the life bus into per-death events and per-segment
    /// summaries, where a segment is the stretch between two checkpoint stamps — "how many times did
    /// players die between CP7 and CP8, and how long did it take them" is the question a level
    /// designer actually asks, and a cumulative death column can't answer it.
    ///
    /// Plain class rather than a component so the bookkeeping is testable without a player loop:
    /// PerformanceRecorder owns one per log file, feeds it the bus events with its own clocks, and
    /// this writes the lines. Every line starts with '#', so readers that only want the sample rows
    /// (difficulty_curve.py, spreadsheets) skip them as comments; the analytics tools read them through
    /// the "#E-schema" lines written once in the header, which name each kind's columns — a reader
    /// locates fields by name, the same contract the sample rows already have.
    ///
    /// A segment opens at scene entry (or recording start) and at every checkpoint stamp, and closes
    /// at the next stamp, a scene change, or the end of recording. Segment ids run per file, not per
    /// scene, so a death's seg column joins to exactly one segment line.
    /// </summary>
    public sealed class CheckpointSegmentTracker
    {
        public const string FromSceneEntry = "entry";
        public const string FromRecordingStart = "rec_start";
        public const string EndCheckpoint = "checkpoint";
        public const string EndScene = "scene";
        public const string EndStop = "stop";

        // A death-free segment shorter than this is noise, not play: an arrival that lands the player
        // on an un-stamped checkpoint stamps it on the spot and would log a zero-length "entry → CP"
        private const double MinEmptySegmentSeconds = 1.0;

        /// <summary>The column layout of each event kind, written once into the file header.</summary>
        public static readonly string[] SchemaLines =
        {
            "#E-schema,death,time_s,play_s,scene,x,y,hit_x,hit_y,cause,source,state,rope,vx,vy,life_s,seg,seg_deaths",
            "#E-schema,respawn,time_s,play_s,scene,x,y,seg",
            "#E-schema,checkpoint,time_s,play_s,scene,x,y,name,seg",
            "#E-schema,segment,time_s,play_s,scene,seg,from,from_x,from_y,to,to_x,to_y,start_s,duration_s,play_dur_s,deaths,end_reason",
            "#E-schema,unstuck,time_s,play_s,scene,x,y,to_x,to_y,method,seg",
        };

        public const string UnstuckPushOut = "pushout";
        public const string UnstuckCheckpoint = "checkpoint";

        private readonly TextWriter writer;

        private bool open;              // a segment is currently being measured
        private string scene;           // scene the open segment belongs to
        private int segmentId = -1;     // id of the open (or last opened) segment
        private string fromName;
        private Vector2 fromPos;
        private bool fromHasPos;        // scene entry / recording start carry no position
        private double startTime;
        private double startPlay;
        private int deaths;

        // Life bookkeeping: a respawn only counts after a death this tracker saw (Respawned is also
        // raised by the scene's initial placement and the Unstuck button), and a life is measured in
        // play time from the last respawn or scene entry
        private bool awaitingRespawn;
        private double lifeStartPlay;

        public CheckpointSegmentTracker(TextWriter writer)
        {
            this.writer = writer ?? throw new ArgumentNullException(nameof(writer));
        }

        public bool IsOpen => open;
        public int SegmentId => segmentId;
        public int SegmentDeaths => deaths;

        public static void WriteSchema(TextWriter target)
        {
            foreach (string line in SchemaLines) target.WriteLine(line);
        }

        /// <summary>A new scene became the playing one: closes what was open (reason "scene") and opens
        /// an entry segment. A null scene (menu, unregistered scene) only closes.</summary>
        public void BeginScene(double time, double play, string sceneName, string from = FromSceneEntry)
        {
            if (open) Close(time, play, EndScene, null, Vector2.zero, false);
            awaitingRespawn = false;
            lifeStartPlay = play;
            if (string.IsNullOrEmpty(sceneName)) return;

            Open(time, play, sceneName, from, Vector2.zero, false);
        }

        /// <summary>A checkpoint was stamped: logs it, closes the running segment there, and opens the
        /// next one from it.</summary>
        public void OnCheckpoint(double time, double play, string sceneName, Vector2 pos, string name)
        {
            EnsureScene(time, play, sceneName);
            string cleanName = Clean(name);

            if (open) Close(time, play, EndCheckpoint, cleanName, pos, true);
            Open(time, play, sceneName, cleanName, pos, true);

            Write("checkpoint", time, play, sceneName,
                F2(pos.x), F2(pos.y), cleanName, segmentId.ToString(CultureInfo.InvariantCulture));
        }

        public void OnDeath(double time, double play, string sceneName, Vector2 pos, Vector2 hit,
                            string cause, string source, string state, string rope, Vector2 velocity)
        {
            EnsureScene(time, play, sceneName);
            if (open) deaths++;

            double life = Math.Max(0.0, play - lifeStartPlay);
            awaitingRespawn = true;

            Write("death", time, play, sceneName,
                F2(pos.x), F2(pos.y), F2(hit.x), F2(hit.y),
                Clean(cause), Clean(source), Clean(state), Clean(rope),
                F2(velocity.x), F2(velocity.y), F2(life),
                open ? segmentId.ToString(CultureInfo.InvariantCulture) : "",
                open ? deaths.ToString(CultureInfo.InvariantCulture) : "");
        }

        /// <summary>Respawned without a preceding death (scene placement, Unstuck) is ignored.</summary>
        public void OnRespawn(double time, double play, string sceneName, Vector2 pos)
        {
            if (!awaitingRespawn) return;
            awaitingRespawn = false;
            lifeStartPlay = play;

            Write("respawn", time, play, sceneName, F2(pos.x), F2(pos.y),
                open ? segmentId.ToString(CultureInfo.InvariantCulture) : "");
        }

        /// <summary>The player used Unstuck: where they were wedged, where they ended up, and how
        /// (pushed out of terrain, or back to the checkpoint). Neither a death nor a segment
        /// boundary — it marks where the level traps players.</summary>
        public void OnUnstuck(double time, double play, string sceneName, Vector2 from, Vector2 to, bool pushedOut)
        {
            EnsureScene(time, play, sceneName);
            Write("unstuck", time, play, sceneName,
                F2(from.x), F2(from.y), F2(to.x), F2(to.y),
                pushedOut ? UnstuckPushOut : UnstuckCheckpoint,
                open ? segmentId.ToString(CultureInfo.InvariantCulture) : "");
        }

        /// <summary>Recording is ending: closes the open segment with reason "stop".</summary>
        public void Stop(double time, double play)
        {
            if (open) Close(time, play, EndStop, null, Vector2.zero, false);
        }

        // ---- internals ----

        // Bus events may arrive for a scene before LevelBus.Started announced it (their init order is
        // undefined), so the scene named by the event itself wins
        private void EnsureScene(double time, double play, string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName)) return;
            if (open && scene == sceneName) return;
            BeginScene(time, play, sceneName);
        }

        private void Open(double time, double play, string sceneName, string from, Vector2 pos, bool hasPos)
        {
            open = true;
            scene = sceneName;
            segmentId++;
            fromName = from;
            fromPos = pos;
            fromHasPos = hasPos;
            startTime = time;
            startPlay = play;
            deaths = 0;
        }

        private void Close(double time, double play, string reason, string toName, Vector2 toPos, bool toHasPos)
        {
            open = false;
            double duration = time - startTime;
            if (deaths == 0 && duration < MinEmptySegmentSeconds) return;

            Write("segment", time, play, scene,
                segmentId.ToString(CultureInfo.InvariantCulture),
                fromName,
                fromHasPos ? F2(fromPos.x) : "", fromHasPos ? F2(fromPos.y) : "",
                toName ?? "",
                toHasPos ? F2(toPos.x) : "", toHasPos ? F2(toPos.y) : "",
                F2(startTime), F2(duration), F2(Math.Max(0.0, play - startPlay)),
                deaths.ToString(CultureInfo.InvariantCulture),
                reason);
        }

        private void Write(string kind, double time, double play, string sceneName, params string[] fields)
        {
            var line = new System.Text.StringBuilder(96);
            line.Append("#E,").Append(kind)
                .Append(',').Append(F2(time))
                .Append(',').Append(F2(play))
                .Append(',').Append(Clean(sceneName));
            foreach (string field in fields) line.Append(',').Append(field);
            writer.WriteLine(line.ToString());
        }

        // Invariant culture for the same reason as the sample rows: a "3,5" decimal would split a field
        private static string F2(double value) => value.ToString("F2", CultureInfo.InvariantCulture);

        // Names come from GameObjects and enums; a stray comma or newline in one must not shift columns
        private static string Clean(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Replace(',', ';').Replace('\n', ' ').Replace('\r', ' ');
        }
    }
}

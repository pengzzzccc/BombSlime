#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Inkform.Tool;
using NUnit.Framework;
using UnityEngine;

namespace Inkform.Tests
{
    /// <summary>
    /// The perf log's event half: CheckpointSegmentTracker's segment bookkeeping (what opens, what
    /// closes, what gets dropped as noise) and the '#E,' line format the analytics tools parse by
    /// schema. Driven directly with hand-picked clocks — no player loop, no scene.
    /// </summary>
    public sealed class PerfLogSegmentTests
    {
        private const string Scene = "Mine Cave 1";

        [Test]
        public void Segment_CountsTheDeathsBetweenTwoStamps()
        {
            var output = new StringWriter();
            var tracker = new CheckpointSegmentTracker(output);

            tracker.BeginScene(0, 0, Scene);
            tracker.OnCheckpoint(10, 10, Scene, new Vector2(100f, 50f), "Checkpoint (7)");
            Death(tracker, 15, 15);
            tracker.OnRespawn(16, 16, Scene, new Vector2(100f, 50f));
            Death(tracker, 20, 20);
            tracker.OnRespawn(21, 21, Scene, new Vector2(100f, 50f));
            tracker.OnCheckpoint(40, 38, Scene, new Vector2(140f, 60f), "Checkpoint (8)");

            List<Dictionary<string, string>> segments = Events(output, "segment");
            Dictionary<string, string> cp7to8 = segments.Find(s => s["from"] == "Checkpoint (7)");
            Assert.IsNotNull(cp7to8, "the CP7 → CP8 stretch is logged as one segment");
            Assert.AreEqual("Checkpoint (8)", cp7to8["to"]);
            Assert.AreEqual("2", cp7to8["deaths"]);
            Assert.AreEqual("checkpoint", cp7to8["end_reason"]);
            Assert.AreEqual("30.00", cp7to8["duration_s"]);
            Assert.AreEqual("28.00", cp7to8["play_dur_s"], "play duration runs on the play clock");
            Assert.AreEqual("100.00", cp7to8["from_x"]);
            Assert.AreEqual("140.00", cp7to8["to_x"]);

            List<Dictionary<string, string>> deaths = Events(output, "death");
            Assert.AreEqual(2, deaths.Count);
            Assert.AreEqual(cp7to8["seg"], deaths[0]["seg"], "a death joins to the segment it happened in");
            Assert.AreEqual("1", deaths[0]["seg_deaths"]);
            Assert.AreEqual("2", deaths[1]["seg_deaths"]);
        }

        [Test]
        public void SceneChange_ClosesTheSegmentAndTheNextOpensFromEntry()
        {
            var output = new StringWriter();
            var tracker = new CheckpointSegmentTracker(output);

            tracker.BeginScene(0, 0, Scene);
            Death(tracker, 5, 5);
            tracker.BeginScene(30, 28, "Mine Cave 2");
            Death(tracker, 40, 38, "Mine Cave 2");
            tracker.Stop(50, 48);

            List<Dictionary<string, string>> segments = Events(output, "segment");
            Assert.AreEqual(2, segments.Count);
            Assert.AreEqual(Scene, segments[0]["scene"]);
            Assert.AreEqual("scene", segments[0]["end_reason"]);
            Assert.AreEqual(CheckpointSegmentTracker.FromSceneEntry, segments[0]["from"]);
            Assert.AreEqual("", segments[0]["from_x"], "scene entry carries no position");

            Assert.AreEqual("Mine Cave 2", segments[1]["scene"]);
            Assert.AreEqual(CheckpointSegmentTracker.FromSceneEntry, segments[1]["from"]);
            Assert.AreEqual("stop", segments[1]["end_reason"]);
            Assert.AreEqual("1", segments[1]["deaths"]);
        }

        [Test]
        public void ShortDeathFreeSegment_IsDroppedAsArrivalNoise()
        {
            var output = new StringWriter();
            var tracker = new CheckpointSegmentTracker(output);

            // Arrival lands the player on an un-stamped machine: entry → CP in the same instant
            tracker.BeginScene(0, 0, Scene);
            tracker.OnCheckpoint(0.2, 0.2, Scene, new Vector2(1f, 1f), "Spawn");
            tracker.OnCheckpoint(30, 30, Scene, new Vector2(9f, 1f), "Checkpoint (1)");

            List<Dictionary<string, string>> segments = Events(output, "segment");
            Assert.AreEqual(1, segments.Count, "the zero-length entry segment is noise, not play");
            Assert.AreEqual("Spawn", segments[0]["from"]);
            Assert.AreEqual(2, Events(output, "checkpoint").Count, "every stamp is still logged");
        }

        [Test]
        public void Respawn_IsOnlyLoggedAfterADeath()
        {
            var output = new StringWriter();
            var tracker = new CheckpointSegmentTracker(output);

            tracker.BeginScene(0, 0, Scene);
            tracker.OnRespawn(1, 1, Scene, Vector2.zero);     // scene placement / Unstuck button
            Assert.AreEqual(0, Events(output, "respawn").Count);

            Death(tracker, 5, 5);
            tracker.OnRespawn(6, 6, Scene, new Vector2(3f, 4f));
            tracker.OnRespawn(7, 7, Scene, new Vector2(3f, 4f));
            Assert.AreEqual(1, Events(output, "respawn").Count, "one death, one respawn");
        }

        [Test]
        public void LifeLength_RunsFromTheLastRespawnOnThePlayClock()
        {
            var output = new StringWriter();
            var tracker = new CheckpointSegmentTracker(output);

            tracker.BeginScene(100, 50, Scene);
            Death(tracker, 112, 58);                            // first life: from scene entry
            tracker.OnRespawn(113, 59, Scene, Vector2.zero);
            Death(tracker, 120, 61.5);                          // second life: from the respawn

            List<Dictionary<string, string>> deaths = Events(output, "death");
            Assert.AreEqual("8.00", deaths[0]["life_s"]);
            Assert.AreEqual("2.50", deaths[1]["life_s"]);
        }

        [Test]
        public void EventsForAnotherScene_OpenThatScenesSegmentFirst()
        {
            var output = new StringWriter();
            var tracker = new CheckpointSegmentTracker(output);

            // LevelBus.Started and the arrival checkpoint stamp have no fixed order: the stamp can
            // land first, still naming the new scene
            tracker.BeginScene(0, 0, Scene);
            Death(tracker, 5, 5);
            tracker.OnCheckpoint(30, 30, "Mine Cave 2", new Vector2(1f, 1f), "Spawn_Door");

            List<Dictionary<string, string>> segments = Events(output, "segment");
            Assert.AreEqual(1, segments.Count);
            Assert.AreEqual(Scene, segments[0]["scene"]);
            Assert.AreEqual("scene", segments[0]["end_reason"], "the old scene's segment closes as a scene change, not at the new scene's checkpoint");
            Assert.AreEqual("Mine Cave 2", Events(output, "checkpoint")[0]["scene"]);
        }

        [Test]
        public void Lines_AreCommentsWithInvariantNumbersAndNoStrayCommas()
        {
            CultureInfo previous = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");     // "3,5" decimal
            try
            {
                var output = new StringWriter();
                var tracker = new CheckpointSegmentTracker(output);
                tracker.BeginScene(0, 0, Scene);
                tracker.OnDeath(3.5, 3.5, Scene, new Vector2(1.25f, -2.5f), new Vector2(1f, -2f),
                    "Spike", "Gear, big", "Fall", "Idle", new Vector2(0f, -14.2f));

                foreach (string line in Lines(output))
                    StringAssert.StartsWith("#", line, "every event line is a comment to sample-row readers");

                Dictionary<string, string> death = Events(output, "death")[0];
                Assert.AreEqual("3.50", death["time_s"]);
                Assert.AreEqual("-2.50", death["y"]);
                Assert.AreEqual("-14.20", death["vy"]);
                Assert.AreEqual("Gear; big", death["source"], "a comma in a name must not shift columns");
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = previous;
            }
        }

        [Test]
        public void Schema_NamesEveryColumnTheTrackerWrites()
        {
            var output = new StringWriter();
            CheckpointSegmentTracker.WriteSchema(output);
            var tracker = new CheckpointSegmentTracker(output);
            tracker.BeginScene(0, 0, Scene);
            tracker.OnCheckpoint(5, 5, Scene, Vector2.one, "A");
            Death(tracker, 6, 6);
            tracker.OnRespawn(7, 7, Scene, Vector2.one);
            tracker.Stop(9, 9);

            Dictionary<string, string[]> schema = Schema(output);
            foreach (string line in Lines(output))
            {
                if (!line.StartsWith("#E,", StringComparison.Ordinal)) continue;
                string[] fields = line.Split(',');
                Assert.IsTrue(schema.ContainsKey(fields[1]), $"kind '{fields[1]}' has a schema line");
                Assert.AreEqual(schema[fields[1]].Length, fields.Length - 2,
                    $"'{fields[1]}' writes exactly the columns its schema names: {line}");
            }
        }

        [Test]
        public void Reader_ParsesTheTrackersOwnOutputBySchema()
        {
            var output = new StringWriter();
            output.WriteLine("# Inkform performance log");
            output.WriteLine("# format=2");
            output.WriteLine("# gpu=Test GPU sysMemMB=8000 quality=Ultra");
            output.WriteLine("time_s,scene,x,y,state,deaths");
            CheckpointSegmentTracker.WriteSchema(output);
            output.WriteLine("0.0,Mine Cave 1,1.00,2.00,Idle,0");
            var tracker = new CheckpointSegmentTracker(output);
            tracker.BeginScene(0, 0, Scene);
            tracker.OnCheckpoint(5, 5, Scene, new Vector2(3f, 4f), "Checkpoint (2)");
            tracker.OnDeath(9, 9, Scene, new Vector2(6f, 4f), new Vector2(6.5f, 3.5f), "Burned", "Laser (3)", "Fall", "Idle", Vector2.zero);
            output.WriteLine("10.0,Mine Cave 1,3.00,4.50,Idle,1");
            tracker.OnRespawn(10, 10, Scene, new Vector2(3f, 4.5f));
            tracker.Stop(20, 20);

            Inkform.EditorTools.PerfLogReader.Log log = Inkform.EditorTools.PerfLogReader.Parse(Lines(output).ToArray(), "perf_20260909_222510");
            Assert.IsNotNull(log);
            Assert.IsTrue(log.HasEvents);
            Assert.AreEqual("09-09 22:25", log.Name);
            Assert.AreEqual("Test GPU", log.Meta["gpu"], "a header value may contain spaces");
            Assert.AreEqual(2, log.Samples.Count);
            Assert.AreEqual(1, log.Deaths.Count);
            Assert.AreEqual("Burned", log.Deaths[0].Cause);
            Assert.AreEqual("Laser (3)", log.Deaths[0].Source);
            Assert.AreEqual(9f, log.Deaths[0].Life, 1e-3f, "a life spans checkpoints: it runs from scene entry or the last respawn");
            Assert.AreEqual(1, log.Respawns.Count);
            Assert.AreEqual(1, log.Checkpoints.Count);
            var stop = log.Segments.Find(g => g.From == "Checkpoint (2)");
            Assert.IsNotNull(stop);
            Assert.AreEqual("stop", stop.EndReason);
            Assert.AreEqual(1, stop.Deaths);
            Assert.IsTrue(stop.HasFrom);
            Assert.IsFalse(stop.HasTo);
        }

        [Test]
        public void Reader_InfersDeathsFromOldLogsAtTheSampleBefore()
        {
            string[] lines =
            {
                "# Inkform performance log",
                "time_s,scene,x,y,state,deaths",
                "0.0,Mine Cave 1,10.00,5.00,Idle,0",
                "1.0,Mine Cave 1,12.00,5.00,Fall,0",
                "2.0,Mine Cave 1,3.00,1.00,Idle,2",     // two deaths since the last sample, already respawned
                "3.0,MainMenu,0.00,0.00,Idle,2",
            };
            Inkform.EditorTools.PerfLogReader.Log log = Inkform.EditorTools.PerfLogReader.Parse(lines, "old");
            Assert.IsFalse(log.HasEvents);
            Assert.AreEqual(2, log.Deaths.Count);
            Assert.IsTrue(log.Deaths[0].Inferred);
            Assert.AreEqual(new Vector2(12f, 5f), log.Deaths[0].Pos, "the row before the rise, not the respawn row");
            Assert.AreEqual("Fall", log.Deaths[0].State);
        }

        [Test]
        public void Reader_RejectsFilesWithoutAHeader()
        {
            Assert.IsNull(Inkform.EditorTools.PerfLogReader.Parse(new[] { "hello", "1,2,3" }, "notes"));
        }

        // ---- helpers ----

        private static void Death(CheckpointSegmentTracker tracker, double time, double play, string scene = Scene) =>
            tracker.OnDeath(time, play, scene, new Vector2(10f, 20f), new Vector2(10.5f, 19.5f),
                "Spike", "spike", "Fall", "Idle", Vector2.zero);

        private static IEnumerable<string> Lines(StringWriter output)
        {
            foreach (string line in output.ToString().Split('\n'))
            {
                string trimmed = line.TrimEnd('\r');
                if (trimmed.Length > 0) yield return trimmed;
            }
        }

        private static Dictionary<string, string[]> Schema(StringWriter output)
        {
            var schema = new Dictionary<string, string[]>();
            var source = new StringWriter();
            CheckpointSegmentTracker.WriteSchema(source);
            foreach (string line in Lines(source))
            {
                string[] parts = line.Split(',');
                var columns = new string[parts.Length - 2];
                Array.Copy(parts, 2, columns, 0, columns.Length);
                schema[parts[1]] = columns;
            }
            return schema;
        }

        // Parses the tracker's output the way the analytics tools do: by the schema's column names
        private static List<Dictionary<string, string>> Events(StringWriter output, string kind)
        {
            Dictionary<string, string[]> schema = Schema(output);
            var events = new List<Dictionary<string, string>>();
            foreach (string line in Lines(output))
            {
                if (!line.StartsWith("#E," + kind + ",", StringComparison.Ordinal)) continue;
                string[] fields = line.Split(',');
                var row = new Dictionary<string, string>();
                string[] columns = schema[kind];
                for (int i = 0; i < columns.Length && i + 2 < fields.Length; i++) row[columns[i]] = fields[i + 2];
                events.Add(row);
            }
            return events;
        }
    }
}
#endif

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Inkform.EditorTools
{
    /// <summary>
    /// Editor-side reader for PerformanceRecorder logs — the same contract the web viewer
    /// (Docs/Tool/PerfLogViewer/js/parse.js) follows: sample columns and '#E,' event fields are
    /// located by the names in the header and the '#E-schema' lines, never by position, so logs from
    /// before and after a column was added both read. A log without event lines (format 1) gets its
    /// deaths inferred from rises of the cumulative deaths column, positioned at the sample before
    /// the rise (the rising row was usually sampled after the respawn).
    /// </summary>
    public static class PerfLogReader
    {
        public sealed class Sample
        {
            public float T;
            public string Scene;
            public Vector2 Pos;
            public string State;
            public int Deaths;
        }

        public sealed class Death
        {
            public float T;
            public string Scene;
            public Vector2 Pos;
            public Vector2 Hit;
            public string Cause = "Unknown";
            public string Source = "";
            public string State = "";
            public float Life = -1f;            // -1: unknown (old logs)
            public int Segment = -1;
            public int SegmentDeath;
            public bool Inferred;
        }

        public sealed class Respawn
        {
            public float T;
            public string Scene;
            public Vector2 Pos;
        }

        public sealed class Stamp
        {
            public float T;
            public string Scene;
            public Vector2 Pos;
            public string Name;
        }

        public sealed class Segment
        {
            public int Id;
            public string Scene;
            public string From, To, EndReason;
            public bool HasFrom, HasTo;
            public Vector2 FromPos, ToPos;
            public float Start, Duration, PlayDuration;
            public int Deaths;
        }

        public sealed class Log
        {
            public string Path;
            public string Name;
            public bool HasEvents;
            public float Interval = 1f;
            public readonly Dictionary<string, string> Meta = new Dictionary<string, string>();
            public readonly List<Sample> Samples = new List<Sample>();
            public readonly List<Death> Deaths = new List<Death>();
            public readonly List<Respawn> Respawns = new List<Respawn>();
            public readonly List<Stamp> Checkpoints = new List<Stamp>();
            public readonly List<Segment> Segments = new List<Segment>();
        }

        public static Log Read(string path)
        {
            Log log = Parse(File.ReadAllLines(path), Path.GetFileNameWithoutExtension(path));
            if (log != null) log.Path = path;
            return log;
        }

        /// <summary>Parses log lines; null when they are not a perf log (no time_s header).</summary>
        public static Log Parse(IList<string> lines, string name)
        {
            var log = new Log { Name = ShortName(name) };
            var schemas = new Dictionary<string, string[]>();
            var events = new List<string[]>();
            string[] header = null;
            int iT = -1, iScene = -1, iX = -1, iY = -1, iState = -1, iDeaths = -1;

            foreach (string raw in lines)
            {
                if (string.IsNullOrEmpty(raw)) continue;
                string line = raw.TrimStart('﻿');

                if (line[0] == '#')
                {
                    if (line.StartsWith("#E-schema,", StringComparison.Ordinal))
                    {
                        string[] parts = line.Split(',');
                        if (parts.Length > 2) schemas[parts[1]] = Slice(parts, 2);
                    }
                    else if (line.StartsWith("#E,", StringComparison.Ordinal)) events.Add(line.Split(','));
                    else ReadMeta(line.TrimStart('#', ' '), log.Meta);
                    continue;
                }

                string[] cols = line.Split(',');
                if (header == null)
                {
                    if (cols[0].Trim() != "time_s") continue;
                    header = cols;
                    iT = Index(header, "time_s"); iScene = Index(header, "scene");
                    iX = Index(header, "x"); iY = Index(header, "y");
                    iState = Index(header, "state"); iDeaths = Index(header, "deaths");
                    continue;
                }
                if (cols.Length < header.Length || !TryFloat(cols[iT], out float t)) continue;
                log.Samples.Add(new Sample
                {
                    T = t,
                    Scene = iScene >= 0 ? cols[iScene].Trim() : "",
                    Pos = new Vector2(Float(cols, iX), Float(cols, iY)),
                    State = iState >= 0 ? cols[iState] : "",
                    Deaths = (int)Float(cols, iDeaths),
                });
            }
            if (header == null || log.Samples.Count == 0) return null;

            log.Interval = MedianInterval(log.Samples);
            log.HasEvents = events.Count > 0 && schemas.Count > 0;
            if (log.HasEvents) ReadEvents(log, schemas, events);
            else InferDeaths(log);
            return log;
        }

        private static void ReadEvents(Log log, Dictionary<string, string[]> schemas, List<string[]> events)
        {
            foreach (string[] parts in events)
            {
                if (parts.Length < 2 || !schemas.TryGetValue(parts[1], out string[] names)) continue;
                var f = new Dictionary<string, string>();
                for (int i = 0; i < names.Length && i + 2 < parts.Length; i++) f[names[i]] = parts[i + 2];

                switch (parts[1])
                {
                    case "death":
                        log.Deaths.Add(new Death
                        {
                            T = Get(f, "time_s"), Scene = Str(f, "scene"),
                            Pos = new Vector2(Get(f, "x"), Get(f, "y")),
                            Hit = new Vector2(Get(f, "hit_x"), Get(f, "hit_y")),
                            Cause = string.IsNullOrEmpty(Str(f, "cause")) ? "Unknown" : Str(f, "cause"),
                            Source = Str(f, "source"), State = Str(f, "state"),
                            Life = Get(f, "life_s", -1f),
                            Segment = (int)Get(f, "seg", -1f), SegmentDeath = (int)Get(f, "seg_deaths", 0f),
                        });
                        break;
                    case "respawn":
                        log.Respawns.Add(new Respawn { T = Get(f, "time_s"), Scene = Str(f, "scene"), Pos = new Vector2(Get(f, "x"), Get(f, "y")) });
                        break;
                    case "checkpoint":
                        log.Checkpoints.Add(new Stamp { T = Get(f, "time_s"), Scene = Str(f, "scene"), Pos = new Vector2(Get(f, "x"), Get(f, "y")), Name = Str(f, "name") });
                        break;
                    case "segment":
                        log.Segments.Add(new Segment
                        {
                            Id = (int)Get(f, "seg", -1f), Scene = Str(f, "scene"),
                            From = Str(f, "from"), To = Str(f, "to"), EndReason = Str(f, "end_reason"),
                            HasFrom = Has(f, "from_x"), HasTo = Has(f, "to_x"),
                            FromPos = new Vector2(Get(f, "from_x"), Get(f, "from_y")),
                            ToPos = new Vector2(Get(f, "to_x"), Get(f, "to_y")),
                            Start = Get(f, "start_s"), Duration = Get(f, "duration_s"), PlayDuration = Get(f, "play_dur_s"),
                            Deaths = (int)Get(f, "deaths"),
                        });
                        break;
                }
            }
        }

        private static void InferDeaths(Log log)
        {
            List<Sample> s = log.Samples;
            for (int i = 1; i < s.Count; i++)
            {
                int rise = s[i].Deaths - s[i - 1].Deaths;
                if (rise <= 0 || rise > 20) continue;
                Sample at = s[i - 1].Scene == s[i].Scene ? s[i - 1] : s[i];
                if (at.Pos == Vector2.zero) continue;
                for (int k = 0; k < rise; k++)
                    log.Deaths.Add(new Death { T = s[i].T, Scene = s[i].Scene, Pos = at.Pos, Hit = at.Pos, State = at.State, Inferred = true });
            }
        }

        // ---- helpers ----

        private static void ReadMeta(string body, Dictionary<string, string> meta)
        {
            if (body.StartsWith("scene=", StringComparison.Ordinal) || body.StartsWith("summary", StringComparison.Ordinal)) return;
            // "key=value key2=value with spaces" — a key is a word directly followed by '='
            int i = 0;
            string key = null;
            int valueStart = 0;
            while (i < body.Length)
            {
                int eq = body.IndexOf('=', i);
                if (eq < 0) break;
                int ks = eq - 1;
                while (ks >= 0 && (char.IsLetterOrDigit(body[ks]) || body[ks] == '_')) ks--;
                ks++;
                if (ks < eq && (ks == 0 || body[ks - 1] == ' '))
                {
                    if (key != null) meta[key] = body.Substring(valueStart, ks - valueStart).Trim();
                    key = body.Substring(ks, eq - ks);
                    valueStart = eq + 1;
                }
                i = eq + 1;
            }
            if (key != null) meta[key] = body.Substring(valueStart).Trim();
        }

        private static float MedianInterval(List<Sample> s)
        {
            var gaps = new List<float>();
            for (int i = 1; i < Mathf.Min(s.Count, 400); i++) gaps.Add(s[i].T - s[i - 1].T);
            if (gaps.Count == 0) return 1f;
            gaps.Sort();
            return Mathf.Max(0.05f, gaps[gaps.Count / 2]);
        }

        private static string ShortName(string file)
        {
            // perf_20260909_222510 → 09-09 22:25
            int at = file.IndexOf("perf_", StringComparison.Ordinal);
            if (at >= 0 && file.Length >= at + 18)
            {
                string d = file.Substring(at + 5, 13);
                if (d[8] == '_') return $"{d.Substring(4, 2)}-{d.Substring(6, 2)} {d.Substring(9, 2)}:{d.Substring(11, 2)}";
            }
            return file;
        }

        private static string[] Slice(string[] parts, int from)
        {
            var o = new string[parts.Length - from];
            Array.Copy(parts, from, o, 0, o.Length);
            return o;
        }

        private static int Index(string[] header, string name)
        {
            for (int i = 0; i < header.Length; i++) if (header[i].Trim() == name) return i;
            return -1;
        }

        private static bool TryFloat(string s, out float v) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

        private static float Float(string[] cols, int i) =>
            i >= 0 && i < cols.Length && TryFloat(cols[i], out float v) ? v : 0f;

        private static float Get(Dictionary<string, string> f, string key, float fallback = 0f) =>
            f.TryGetValue(key, out string s) && TryFloat(s, out float v) ? v : fallback;

        private static bool Has(Dictionary<string, string> f, string key) =>
            f.TryGetValue(key, out string s) && s.Length > 0;

        private static string Str(Dictionary<string, string> f, string key) =>
            f.TryGetValue(key, out string s) ? s : "";
    }
}

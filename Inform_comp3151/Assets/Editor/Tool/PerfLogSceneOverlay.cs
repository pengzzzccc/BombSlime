using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Inkform.EditorTools
{
    /// <summary>
    /// Perf-log deaths, paths and segment difficulty drawn straight onto the Scene view, over the
    /// real level art — the in-editor sibling of Docs/Tool/PerfLogViewer, for when the question is
    /// "which tile, exactly" and the answer should be one click from fixing it.
    ///
    /// Only the open scene's data is drawn (logs carry scene names; switching scenes switches data).
    /// The death heat is a blurred grid rendered into a texture on a hidden, never-saved sprite
    /// (HideAndDontSave) above everything else — Handles can't draw a smooth field, and a sprite gets
    /// bilinear filtering for free. That object is rebuilt on demand and destroyed whenever the window
    /// closes, the scene changes, play mode starts or scripts reload, so it can never end up in a scene.
    ///
    /// Loaded log paths are remembered per machine (EditorPrefs) and reloaded when the window opens.
    /// </summary>
    public sealed class PerfLogSceneOverlay : EditorWindow
    {
        private const string PathsPrefKey = "Inkform.PerfLogOverlay.Paths";
        private const float HeatCell = 0.5f;
        private const float HeatSigma = 1.3f;

        private sealed class Entry
        {
            public PerfLogReader.Log Log;
            public bool Enabled = true;
            public int Slot;
        }

        private readonly List<Entry> entries = new List<Entry>();
        private readonly HashSet<string> hiddenCauses = new HashSet<string>();

        [SerializeField] private bool showDeaths = true;
        [SerializeField] private bool showHeat = true;
        [SerializeField] private bool showPaths = true;
        [SerializeField] private bool showSegments = true;
        [SerializeField] private float heatOpacity = 0.8f;
        [SerializeField] private float markerSize = 1f;
        [SerializeField] private float pathOpacity = 0.55f;
        [SerializeField] private bool foldLayers = true, foldCauses = true, foldLook = false;

        // per-scene cache, rebuilt when the scene, the selection or a filter changes
        private string cachedScene;
        private bool cacheDirty = true;
        private readonly List<(PerfLogReader.Death death, Entry entry)> deaths = new List<(PerfLogReader.Death, Entry)>();
        private readonly List<(Vector3[] points, Color color)> paths = new List<(Vector3[], Color)>();
        private readonly List<SegmentLabel> segmentLabels = new List<SegmentLabel>();
        private Vector2 hottest;
        private bool hasHottest;

        private GameObject heatObject;
        private Texture2D heatTexture;
        private Material heatMaterial;

        private Vector2 scroll;
        private GUIStyle pillStyle, pillTitleStyle;
        private Texture2D pillTexture;

        private struct SegmentLabel
        {
            public Vector3 Pos;
            public string Text;
            public Color Color;
        }

        [MenuItem("Tools/Inkform/Analytics/PerfLog Scene Overlay")]
        private static void Open()
        {
            var w = GetWindow<PerfLogSceneOverlay>();
            w.titleContent = new GUIContent("PerfLog Overlay");
            w.minSize = new Vector2(300, 360);
            w.Show();
        }

        private void OnEnable()
        {
            SceneView.duringSceneGui += OnSceneGUI;
            EditorSceneManager.activeSceneChangedInEditMode += OnSceneChanged;
            EditorApplication.playModeStateChanged += OnPlayMode;
            AssemblyReloadEvents.beforeAssemblyReload += DestroyHeat;

            if (entries.Count == 0)
                foreach (string path in EditorPrefs.GetString(PathsPrefKey, "").Split('|'))
                    if (!string.IsNullOrEmpty(path) && File.Exists(path)) AddLog(path, save: false);
            cacheDirty = true;
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            EditorSceneManager.activeSceneChangedInEditMode -= OnSceneChanged;
            EditorApplication.playModeStateChanged -= OnPlayMode;
            AssemblyReloadEvents.beforeAssemblyReload -= DestroyHeat;
            DestroyHeat();
            if (pillTexture != null) DestroyImmediate(pillTexture);
            SceneView.RepaintAll();
        }

        private void OnSceneChanged(Scene from, Scene to) { DestroyHeat(); Invalidate(); }

        private void OnPlayMode(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode) DestroyHeat();
            if (change == PlayModeStateChange.EnteredEditMode) Invalidate();
        }

        private void Invalidate()
        {
            cacheDirty = true;
            Repaint();
            SceneView.RepaintAll();
        }

        // ---------- loading ----------

        private void AddLog(string path, bool save = true)
        {
            if (entries.Any(e => e.Log.Path == path)) return;
            PerfLogReader.Log log;
            try { log = PerfLogReader.Read(path); }
            catch (IOException e) { Debug.LogWarning($"[PerfLog] could not read {path}: {e.Message}"); return; }
            if (log == null) { Debug.LogWarning($"[PerfLog] {Path.GetFileName(path)} is not a perf log"); return; }

            int slot = 0;
            while (entries.Any(e => e.Slot == slot)) slot++;
            entries.Add(new Entry { Log = log, Slot = slot });
            if (save) SavePaths();
            Invalidate();
        }

        private void SavePaths() => EditorPrefs.SetString(PathsPrefKey, string.Join("|", entries.Select(e => e.Log.Path)));

        // ---------- window ----------

        private void OnGUI()
        {
            string scene = SceneManager.GetActiveScene().name;
            EnsureCache(scene);

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button("Add logs…", EditorStyles.toolbarButton))
                {
                    string dir = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "Docs", "PerfLogs");
                    string path = EditorUtility.OpenFilePanelWithFilters("Add perf log", Directory.Exists(dir) ? dir : "", new[] { "Perf log", "csv" });
                    if (!string.IsNullOrEmpty(path)) AddLog(path);
                }
                if (GUILayout.Button("Viewer", EditorStyles.toolbarButton)) EditorApplication.ExecuteMenuItem("Tools/Inkform/Analytics/Open PerfLog Viewer");
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(entries.Count == 0))
                    if (GUILayout.Button("Clear", EditorStyles.toolbarButton)) { entries.Clear(); SavePaths(); DestroyHeat(); Invalidate(); }
            }

            DropArea();

            scroll = EditorGUILayout.BeginScrollView(scroll);
            LogList(scene);

            foldLayers = EditorGUILayout.Foldout(foldLayers, "Layers", true);
            if (foldLayers)
            {
                EditorGUI.BeginChangeCheck();
                showDeaths = EditorGUILayout.ToggleLeft("Death points", showDeaths);
                showHeat = EditorGUILayout.ToggleLeft("Death heat", showHeat);
                showPaths = EditorGUILayout.ToggleLeft("Paths", showPaths);
                showSegments = EditorGUILayout.ToggleLeft("Segment difficulty labels", showSegments);
                if (EditorGUI.EndChangeCheck()) { if (!showHeat) DestroyHeat(); Invalidate(); }
            }

            foldCauses = EditorGUILayout.Foldout(foldCauses, "Death causes", true);
            if (foldCauses) CauseToggles(scene);

            foldLook = EditorGUILayout.Foldout(foldLook, "Look", true);
            if (foldLook)
            {
                EditorGUI.BeginChangeCheck();
                heatOpacity = EditorGUILayout.Slider("Heat opacity", heatOpacity, 0.1f, 1f);
                markerSize = EditorGUILayout.Slider("Marker size", markerSize, 0.5f, 2f);
                pathOpacity = EditorGUILayout.Slider("Path opacity", pathOpacity, 0.1f, 1f);
                if (EditorGUI.EndChangeCheck())
                {
                    if (heatObject != null) heatObject.GetComponent<SpriteRenderer>().color = new Color(1, 1, 1, heatOpacity);
                    SceneView.RepaintAll();
                }
            }

            EditorGUILayout.Space(6);
            using (new EditorGUI.DisabledScope(!hasHottest))
                if (GUILayout.Button("Focus the deadliest spot", GUILayout.Height(24))) FocusHottest();
            EditorGUILayout.EndScrollView();

            // status bar
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                GUILayout.Label($"{entries.Count} logs loaded · {scene}: {deaths.Count} deaths shown", EditorStyles.miniLabel);
        }

        private void DropArea()
        {
            Rect r = GUILayoutUtility.GetRect(0, 38, GUILayout.ExpandWidth(true));
            r = new Rect(r.x + 6, r.y + 6, r.width - 12, r.height - 6);
            GUI.Box(r, "Drop perf_*.csv files here", EditorStyles.helpBox);
            Event e = Event.current;
            if (!r.Contains(e.mousePosition) || (e.type != EventType.DragUpdated && e.type != EventType.DragPerform)) return;
            bool any = DragAndDrop.paths.Any(p => p.EndsWith(".csv", System.StringComparison.OrdinalIgnoreCase));
            DragAndDrop.visualMode = any ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
            if (e.type == EventType.DragPerform && any)
            {
                DragAndDrop.AcceptDrag();
                foreach (string p in DragAndDrop.paths) if (p.EndsWith(".csv", System.StringComparison.OrdinalIgnoreCase)) AddLog(Path.GetFullPath(p));
            }
            e.Use();
        }

        private void LogList(string scene)
        {
            Entry remove = null;
            foreach (Entry entry in entries)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUI.BeginChangeCheck();
                    entry.Enabled = EditorGUILayout.Toggle(entry.Enabled, GUILayout.Width(16));
                    if (EditorGUI.EndChangeCheck()) Invalidate();
                    Rect sw = GUILayoutUtility.GetRect(10, 10, GUILayout.Width(10), GUILayout.Height(16));
                    EditorGUI.DrawRect(new Rect(sw.x, sw.y + 4, 10, 10), PerfLogStyle.SessionColor(entry.Slot));
                    int n = entry.Log.Deaths.Count(d => d.Scene == scene);
                    string kind = entry.Log.HasEvents ? "events" : "inferred";
                    GUILayout.Label(new GUIContent($"{entry.Log.Name}   ☠ {n}   ({kind})", entry.Log.Path));
                    if (GUILayout.Button("✕", EditorStyles.miniButton, GUILayout.Width(22))) remove = entry;
                }
            }
            if (remove != null) { entries.Remove(remove); SavePaths(); DestroyHeat(); Invalidate(); }
            if (entries.Count == 0) EditorGUILayout.HelpBox("No logs yet. Logs live in Docs/PerfLogs/ (turn on Video › Perf Recording in game).", MessageType.Info);
            EditorGUILayout.Space(4);
        }

        private void CauseToggles(string scene)
        {
            var counts = new Dictionary<string, int>();
            foreach (Entry e in entries)
            foreach (PerfLogReader.Death d in e.Log.Deaths)
            {
                if (d.Scene != scene) continue;
                string c = d.Inferred ? "Unknown" : d.Cause;
                counts[c] = counts.TryGetValue(c, out int n) ? n + 1 : 1;
            }
            if (counts.Count == 0) { EditorGUILayout.LabelField("No deaths in this scene", EditorStyles.miniLabel); return; }
            foreach (KeyValuePair<string, int> kv in counts.OrderByDescending(k => k.Value))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    Rect sw = GUILayoutUtility.GetRect(10, 10, GUILayout.Width(10), GUILayout.Height(16));
                    EditorGUI.DrawRect(new Rect(sw.x, sw.y + 4, 10, 10), PerfLogStyle.CauseColor(kv.Key));
                    bool on = !hiddenCauses.Contains(kv.Key);
                    bool next = EditorGUILayout.ToggleLeft($"{kv.Key}  ({kv.Value})", on);
                    if (next == on) continue;
                    if (next) hiddenCauses.Remove(kv.Key); else hiddenCauses.Add(kv.Key);
                    DestroyHeat();
                    Invalidate();
                }
            }
        }

        // ---------- cache ----------

        private void EnsureCache(string scene)
        {
            if (!cacheDirty && cachedScene == scene) return;
            cacheDirty = false;
            cachedScene = scene;
            deaths.Clear();
            paths.Clear();
            segmentLabels.Clear();
            hasHottest = false;

            foreach (Entry entry in entries)
            {
                if (!entry.Enabled) continue;
                PerfLogReader.Log log = entry.Log;
                foreach (PerfLogReader.Death d in log.Deaths)
                    if (d.Scene == scene && !hiddenCauses.Contains(d.Inferred ? "Unknown" : d.Cause)) deaths.Add((d, entry));
                BuildPaths(entry, scene);
            }
            BuildSegments(scene);

            if (deaths.Count > 0)
            {
                // densest 3×3-unit neighbourhood, for the focus button
                var cells = new Dictionary<Vector2Int, int>();
                foreach (var (d, _) in deaths)
                {
                    var k = new Vector2Int(Mathf.FloorToInt(d.Pos.x / 3f), Mathf.FloorToInt(d.Pos.y / 3f));
                    cells[k] = cells.TryGetValue(k, out int n) ? n + 1 : 1;
                }
                Vector2Int best = cells.OrderByDescending(c => c.Value).First().Key;
                hottest = new Vector2((best.x + 0.5f) * 3f, (best.y + 0.5f) * 3f);
                hasHottest = true;
            }
        }

        private void BuildPaths(Entry entry, string scene)
        {
            PerfLogReader.Log log = entry.Log;
            float maxGap = Mathf.Max(2.5f * log.Interval, 1.2f);
            var run = new List<Vector3>();
            int di = 0;
            var sorted = log.Deaths.Where(d => d.Scene == scene).OrderBy(d => d.T).ToList();
            PerfLogReader.Sample prev = null;
            void Flush()
            {
                if (run.Count >= 2) paths.Add((run.ToArray(), PerfLogStyle.SessionColor(entry.Slot)));
                run.Clear();
            }
            foreach (PerfLogReader.Sample s in log.Samples)
            {
                if (s.Scene != scene || s.Pos == Vector2.zero) { Flush(); prev = null; continue; }
                if (prev != null)
                {
                    bool died = false;
                    while (di < sorted.Count && sorted[di].T <= s.T)
                    {
                        if (sorted[di].T >= prev.T) { run.Add(sorted[di].Pos); died = true; }
                        di++;
                    }
                    if (died || s.T - prev.T > maxGap || (s.Pos - prev.Pos).sqrMagnitude > 144f) Flush();
                }
                run.Add(s.Pos);
                prev = s;
            }
            Flush();
        }

        private void BuildSegments(string scene)
        {
            var groups = new Dictionary<string, List<PerfLogReader.Segment>>();
            foreach (Entry entry in entries)
            {
                if (!entry.Enabled) continue;
                foreach (PerfLogReader.Segment g in entry.Log.Segments)
                {
                    if (g.Scene != scene || !g.HasTo) continue;
                    string key = $"{g.From}>{g.To}@{g.ToPos.x:F1},{g.ToPos.y:F1}";
                    if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<PerfLogReader.Segment>();
                    list.Add(g);
                }
            }
            foreach (var list in groups.Values)
            {
                int total = list.Sum(g => g.Deaths);
                float mean = total / (float)list.Count;
                var times = list.Select(g => g.PlayDuration > 0 ? g.PlayDuration : g.Duration).OrderBy(v => v).ToList();
                float median = times[times.Count / 2];
                Color color = PerfLogStyle.Rating(mean, out string rating);
                segmentLabels.Add(new SegmentLabel
                {
                    Pos = list[0].ToPos + Vector2.up * 2.2f,
                    Text = $"← {list[0].From}   ☠ {total} · {(int)median / 60}:{(int)median % 60:00} · {rating}",
                    Color = color,
                });
            }
        }

        // ---------- scene drawing ----------

        private void OnSceneGUI(SceneView view)
        {
            if (entries.Count == 0) return;
            string scene = SceneManager.GetActiveScene().name;
            EnsureCache(scene);
            Event e = Event.current;
            if (e.type == EventType.MouseMove) { view.Repaint(); return; }
            if (e.type != EventType.Repaint) return;

            if (showHeat && !EditorApplication.isPlaying) EnsureHeat();
            if (heatObject != null) heatObject.SetActive(showHeat);

            if (showPaths) DrawPaths();
            if (showDeaths) DrawDeaths();
            if (showSegments || showDeaths) DrawGui(e.mousePosition);
        }

        private void DrawPaths()
        {
            Color prevColor = Handles.color;
            foreach (var (points, color) in paths)
            {
                Handles.color = new Color(color.r, color.g, color.b, pathOpacity);
                Handles.DrawAAPolyLine(3f, points);
            }
            Handles.color = prevColor;
        }

        private void DrawDeaths()
        {
            Color prevColor = Handles.color;
            foreach (var (d, entry) in deaths)
            {
                Vector3 p = d.Pos;
                float r = HandleUtility.GetHandleSize(p) * 0.055f * markerSize;
                PerfLogStyle.Shape shape = PerfLogStyle.CauseShape(d.Cause, d.Inferred);
                Color c = PerfLogStyle.CauseColor(d.Inferred ? "Unknown" : d.Cause);
                Vector3[] outline = ShapePoints(shape, p, r);

                if (shape == PerfLogStyle.Shape.Ring)
                {
                    Handles.color = PerfLogStyle.Surface;
                    Handles.DrawWireDisc(p, Vector3.forward, r * 0.9f, 5f);
                    Handles.color = c;
                    Handles.DrawWireDisc(p, Vector3.forward, r * 0.9f, 2.5f);
                    continue;
                }
                // a dark ring under every marker keeps overlapping deaths individually readable
                Handles.color = PerfLogStyle.Surface;
                Handles.DrawAAPolyLine(4f, Close(outline));
                Handles.color = c;
                Handles.DrawAAConvexPolygon(outline);
            }
            Handles.color = prevColor;
        }

        private static Vector3[] Close(Vector3[] pts)
        {
            var o = new Vector3[pts.Length + 1];
            pts.CopyTo(o, 0);
            o[pts.Length] = pts[0];
            return o;
        }

        private static Vector3[] ShapePoints(PerfLogStyle.Shape shape, Vector3 c, float r)
        {
            switch (shape)
            {
                case PerfLogStyle.Shape.Triangle:
                    return new[] { c + new Vector3(0, r * 1.1f), c + new Vector3(r, -r * 0.75f), c + new Vector3(-r, -r * 0.75f) };
                case PerfLogStyle.Shape.Diamond:
                    return new[] { c + new Vector3(0, r * 1.15f), c + new Vector3(r * 0.9f, 0), c + new Vector3(0, -r * 1.15f), c + new Vector3(-r * 0.9f, 0) };
                case PerfLogStyle.Shape.Square:
                    return new[] { c + new Vector3(-r * .82f, -r * .82f), c + new Vector3(r * .82f, -r * .82f), c + new Vector3(r * .82f, r * .82f), c + new Vector3(-r * .82f, r * .82f) };
                default:
                {
                    var pts = new Vector3[16];
                    for (int i = 0; i < pts.Length; i++)
                    {
                        float a = i * Mathf.PI * 2f / pts.Length;
                        pts[i] = c + new Vector3(Mathf.Cos(a), Mathf.Sin(a)) * r * 0.9f;
                    }
                    return pts;
                }
            }
        }

        private void DrawGui(Vector2 mouse)
        {
            EnsureStyles();
            Handles.BeginGUI();

            if (showSegments)
            {
                foreach (SegmentLabel label in segmentLabels)
                {
                    Vector2 gp = HandleUtility.WorldToGUIPoint(label.Pos);
                    Pill(gp, label.Text, label.Color);
                }
            }

            if (showDeaths)
            {
                // hover: nearest death within 12 px of the cursor gets a detail card
                (PerfLogReader.Death death, Entry entry) best = default;
                float bestD = 12f;
                foreach (var item in deaths)
                {
                    float dist = Vector2.Distance(HandleUtility.WorldToGUIPoint(item.death.Pos), mouse);
                    if (dist < bestD) { bestD = dist; best = item; }
                }
                if (best.death != null) Card(mouse, best.death, best.entry);
            }
            Handles.EndGUI();
        }

        private void Pill(Vector2 center, string text, Color accent)
        {
            var content = new GUIContent(text);
            Vector2 size = pillStyle.CalcSize(content);
            var r = new Rect(center.x - size.x / 2f, center.y - size.y / 2f, size.x, size.y);
            Color prev = GUI.color;
            GUI.color = new Color(1, 1, 1, 0.94f);
            GUI.Box(r, content, pillStyle);
            GUI.color = prev;
            EditorGUI.DrawRect(new Rect(r.x + 3, r.y + 4, 3, r.height - 8), accent);
        }

        private void Card(Vector2 mouse, PerfLogReader.Death d, Entry entry)
        {
            string title = d.Inferred ? "Death (inferred from an old log)" : string.IsNullOrEmpty(d.Source) ? d.Cause : $"{d.Cause} · {d.Source}";
            string body = $"Session  {entry.Log.Name}\nState  {(string.IsNullOrEmpty(d.State) ? "–" : d.State)}";
            if (d.Life >= 0) body += $"\nLife  {d.Life:F1} s";
            if (d.Segment >= 0) body += $"\nSegment #{d.Segment} · death {d.SegmentDeath}";
            body += $"\nAt  {d.Pos.x:F1}, {d.Pos.y:F1}";
            var titleContent = new GUIContent(title);
            var bodyContent = new GUIContent(body);
            Vector2 ts = pillTitleStyle.CalcSize(titleContent), bs = pillStyle.CalcSize(bodyContent);
            float w = Mathf.Max(ts.x, bs.x) + 8, h = ts.y + bs.y + 4;
            var r = new Rect(mouse.x + 16, mouse.y + 16, w, h);
            GUI.Box(r, GUIContent.none, pillStyle);
            EditorGUI.DrawRect(new Rect(r.x + 3, r.y + 5, 3, r.height - 10), PerfLogStyle.CauseColor(d.Inferred ? "Unknown" : d.Cause));
            GUI.Label(new Rect(r.x, r.y + 2, w, ts.y), titleContent, pillTitleStyle);
            GUI.Label(new Rect(r.x, r.y + ts.y, w, bs.y), bodyContent, pillStyle);
        }

        // Rounded translucent pill, 9-sliced from a tiny generated texture
        private void EnsureStyles()
        {
            if (pillStyle != null && pillTexture != null) return;
            const int size = 16, radius = 6;
            pillTexture = new Texture2D(size, size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            Color fill = PerfLogStyle.Surface;
            fill.a = 0.92f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = Mathf.Max(0, Mathf.Max(radius - x - 0.5f, x + 0.5f - (size - radius))),
                      dy = Mathf.Max(0, Mathf.Max(radius - y - 0.5f, y + 0.5f - (size - radius)));
                float a = Mathf.Clamp01(radius - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f);
                pillTexture.SetPixel(x, y, new Color(fill.r, fill.g, fill.b, fill.a * a));
            }
            pillTexture.Apply();
            pillStyle = new GUIStyle(EditorStyles.label)
            {
                normal = { background = pillTexture, textColor = PerfLogStyle.Ink },
                border = new RectOffset(radius, radius, radius, radius),
                padding = new RectOffset(12, 10, 4, 5),
                fontSize = 11,
                richText = false,
            };
            pillTitleStyle = new GUIStyle(pillStyle) { fontStyle = FontStyle.Bold, normal = { background = null, textColor = PerfLogStyle.Ink } };
        }

        // ---------- heat ----------

        private void EnsureHeat()
        {
            if (heatObject != null || deaths.Count == 0) return;

            Vector2 min = deaths[0].death.Pos, max = min;
            foreach (var (d, _) in deaths) { min = Vector2.Min(min, d.Pos); max = Vector2.Max(max, d.Pos); }
            min -= Vector2.one * 6f; max += Vector2.one * 6f;
            int w = Mathf.CeilToInt((max.x - min.x) / HeatCell), h = Mathf.CeilToInt((max.y - min.y) / HeatCell);
            if (w <= 0 || h <= 0 || w * h > 8_000_000) return;

            var grid = new float[w * h];
            foreach (var (d, _) in deaths)
            {
                int gx = Mathf.FloorToInt((d.Pos.x - min.x) / HeatCell), gy = Mathf.FloorToInt((d.Pos.y - min.y) / HeatCell);
                if (gx >= 0 && gy >= 0 && gx < w && gy < h) grid[gx + gy * w] += 1f;
            }
            Blur(grid, w, h, HeatSigma / HeatCell);
            float top = Percentile(grid, 0.995f);
            if (top <= 0f) return;

            heatTexture = new Texture2D(w, h, TextureFormat.RGBA32, false, false)
            {
                hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp,
            };
            var pixels = new Color32[w * h];
            for (int i = 0; i < pixels.Length; i++)
            {
                float t = grid[i] / top;
                pixels[i] = t <= 0.02f ? new Color32(0, 0, 0, 0) : (Color32)PerfLogStyle.Ramp(PerfLogStyle.DeathRamp, t);
            }
            heatTexture.SetPixels32(pixels);
            heatTexture.Apply(false);

            Shader shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default") ?? Shader.Find("Sprites/Default");
            heatMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };

            heatObject = new GameObject("PerfLog Heat (overlay)") { hideFlags = HideFlags.HideAndDontSave };
            heatObject.transform.position = new Vector3(min.x, min.y, 0f);
            var sr = heatObject.AddComponent<SpriteRenderer>();
            sr.sprite = Sprite.Create(heatTexture, new Rect(0, 0, w, h), Vector2.zero, 1f / HeatCell);
            sr.sprite.hideFlags = HideFlags.HideAndDontSave;
            sr.sharedMaterial = heatMaterial;
            sr.color = new Color(1, 1, 1, heatOpacity);
            SortingLayer[] layers = SortingLayer.layers;
            if (layers.Length > 0) sr.sortingLayerID = layers[layers.Length - 1].id;
            sr.sortingOrder = short.MaxValue;
        }

        private void DestroyHeat()
        {
            if (heatObject != null)
            {
                var sr = heatObject.GetComponent<SpriteRenderer>();
                if (sr != null && sr.sprite != null) DestroyImmediate(sr.sprite);
                DestroyImmediate(heatObject);
            }
            if (heatTexture != null) DestroyImmediate(heatTexture);
            if (heatMaterial != null) DestroyImmediate(heatMaterial);
            heatObject = null; heatTexture = null; heatMaterial = null;
        }

        private static void Blur(float[] v, int w, int h, float sigma)
        {
            int r = Mathf.CeilToInt(sigma * 3f);
            var k = new float[r * 2 + 1];
            float sum = 0f;
            for (int i = -r; i <= r; i++) { k[i + r] = Mathf.Exp(-(i * i) / (2f * sigma * sigma)); sum += k[i + r]; }
            for (int i = 0; i < k.Length; i++) k[i] /= sum;
            var tmp = new float[v.Length];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float acc = 0f;
                for (int i = Mathf.Max(-r, -x); i <= Mathf.Min(r, w - 1 - x); i++) acc += v[y * w + x + i] * k[i + r];
                tmp[y * w + x] = acc;
            }
            for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
            {
                float acc = 0f;
                for (int i = Mathf.Max(-r, -y); i <= Mathf.Min(r, h - 1 - y); i++) acc += tmp[(y + i) * w + x] * k[i + r];
                v[y * w + x] = acc;
            }
        }

        private static float Percentile(float[] v, float q)
        {
            var nz = v.Where(x => x > 0f).ToList();
            if (nz.Count == 0) return 0f;
            nz.Sort();
            return nz[Mathf.Min(nz.Count - 1, (int)(nz.Count * q))];
        }

        private void FocusHottest()
        {
            SceneView view = SceneView.lastActiveSceneView;
            if (view == null || !hasHottest) return;
            view.Frame(new Bounds(new Vector3(hottest.x, hottest.y, 0f), new Vector3(14f, 9f, 1f)), false);
        }
    }
}

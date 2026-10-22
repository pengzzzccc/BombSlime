using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Inkform.Fx;
using Inkform.Interactable.Parts;
using Inkform.Level;
using Inkform.Life;
using Inkform.Tool;
using Inkform.WorldTools;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.Tilemaps;
using Object = UnityEngine.Object;

namespace Inkform.EditorTools
{
    /// <summary>
    /// Dumps every World room's layout for the PerfLog viewer (Docs/Tool/PerfLogViewer): tile
    /// occupancy per tilemap as a bitset, checkpoints, doors, hazards with their patrol routes,
    /// interactive props and camera walls — all in world units, the same space the perf log's x/y
    /// columns are in, so the viewer can lay death points and paths straight onto the level.
    ///
    /// Output is data/level_layouts.js (window.INKFORM_LAYOUTS = …) so the viewer loads it with a
    /// plain script tag even from file://, where fetch() of a .json is blocked; the same object is
    /// also written as level_layouts.json for anything else that wants it.
    ///
    /// The "+ Art" variant also renders each room's real art through a temporary orthographic camera,
    /// chunk by chunk, into one JPG (parallax layers and the player hidden — they only make sense
    /// relative to a moving camera). A small sidecar per image keeps its world rectangle, so a later
    /// geometry-only export still links the art it didn't re-render.
    ///
    /// Scenes are walked like WorldValidator's deep pass: offer to save, open each room Single, put
    /// the original scene back at the end. Nothing is saved; art-time hiding dies with the reload.
    /// </summary>
    public static class LevelLayoutExporter
    {
        private const string MenuRoot = "Tools/Inkform/Analytics/";
        private const string PpuPrefKey = "Inkform.LevelArtPixelsPerUnit";
        private const int DefaultPpu = 12;
        private const int ChunkPixels = 1024;           // render target edge per chunk
        private const int MaxImageEdge = 16384;         // past this, ppu is lowered to fit
        private const int JpgQuality = 90;

        private const int TerrainLayer = 6;
        private const int BreakableLayer = 11;
        private const int HazardLayer = 13;
        private const int BackgroundSortingOrder = -20; // background tilemaps sit at -20 / -30

        public static string ViewerFolder =>
            Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "Docs", "Tool", "PerfLogViewer");
        public static string DataFolder => Path.Combine(ViewerFolder, "data");

        // Scenes outside the World's room list (test / blockout levels like Lab_build) that were
        // exported with "Export Current Scene + Art": one asset path per line. Kept in the data
        // folder so it travels with the exported data — every later export keeps them in.
        // Delete a line (and its .jpg / .art.json) to drop that scene again.
        private static string ExtraScenesPath => Path.Combine(DataFolder, "extra_scenes.txt");

        [MenuItem(MenuRoot + "Export Level Layouts")]
        private static void ExportLayoutsOnly() => Export(_ => false);

        [MenuItem(MenuRoot + "Export Level Layouts + Art")]
        private static void ExportWithArt() => Export(_ => true);

        /// <summary>The open scene's layout and art, for a scene the World does not list (or a
        /// room whose art alone needs refreshing). The other scenes are re-collected too — the
        /// layout file is written whole — but keep their already-rendered art, so only this
        /// scene pays for rendering.</summary>
        [MenuItem(MenuRoot + "Export Current Scene + Art")]
        private static void ExportCurrentScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            string current = SceneManager.GetActiveScene().path;
            if (string.IsNullOrEmpty(current))
            {
                EditorUtility.DisplayDialog("Level layout export", "Save the scene first — an unsaved scene has no path to export.", "OK");
                return;
            }

            WorldDefinition world = WorldSetup.FindWorld();
            bool isRoom = false;
            if (world != null)
                foreach (RoomDefinition room in world.Rooms)
                    if (room != null && room.IsSet && AssetDatabase.GetAssetPath(room.Scene.SceneAsset) == current)
                        isRoom = true;

            if (!isRoom)
            {
                List<string> extras = ReadExtraScenes();
                if (!extras.Contains(current))
                {
                    extras.Add(current);
                    Directory.CreateDirectory(DataFolder);
                    File.WriteAllLines(ExtraScenesPath, extras, new UTF8Encoding(false));
                }
            }

            Export(path => path == current, saveFirst: false);
        }

        [MenuItem(MenuRoot + "Export Current Scene + Art", true)]
        private static bool ExportCurrentSceneOn() => !EditorApplication.isPlaying;

        private static List<string> ReadExtraScenes()
        {
            var paths = new List<string>();
            if (!File.Exists(ExtraScenesPath)) return paths;
            foreach (string line in File.ReadAllLines(ExtraScenesPath))
            {
                string path = line.Trim();
                if (path.Length > 0 && !paths.Contains(path)) paths.Add(path);
            }
            return paths;
        }

        [MenuItem(MenuRoot + "Open PerfLog Viewer")]
        private static void OpenViewer()
        {
            string index = Path.Combine(ViewerFolder, "index.html");
            if (!File.Exists(index))
            {
                EditorUtility.DisplayDialog("PerfLog Viewer", $"Viewer not found at\n{index}", "OK");
                return;
            }
            Application.OpenURL(new Uri(index).AbsoluteUri);
        }

        [MenuItem(MenuRoot + "Art Resolution/8 px per unit")] private static void Ppu8() => EditorPrefs.SetInt(PpuPrefKey, 8);
        [MenuItem(MenuRoot + "Art Resolution/12 px per unit (default)")] private static void Ppu12() => EditorPrefs.SetInt(PpuPrefKey, 12);
        [MenuItem(MenuRoot + "Art Resolution/16 px per unit")] private static void Ppu16() => EditorPrefs.SetInt(PpuPrefKey, 16);
        [MenuItem(MenuRoot + "Art Resolution/24 px per unit")] private static void Ppu24() => EditorPrefs.SetInt(PpuPrefKey, 24);
        [MenuItem(MenuRoot + "Art Resolution/32 px per unit")] private static void Ppu32() => EditorPrefs.SetInt(PpuPrefKey, 32);

        [MenuItem(MenuRoot + "Art Resolution/8 px per unit", true)] private static bool Ppu8On() => Check(8);
        [MenuItem(MenuRoot + "Art Resolution/12 px per unit (default)", true)] private static bool Ppu12On() => Check(12);
        [MenuItem(MenuRoot + "Art Resolution/16 px per unit", true)] private static bool Ppu16On() => Check(16);
        [MenuItem(MenuRoot + "Art Resolution/24 px per unit", true)] private static bool Ppu24On() => Check(24);
        [MenuItem(MenuRoot + "Art Resolution/32 px per unit", true)] private static bool Ppu32On() => Check(32);

        private static bool Check(int ppu)
        {
            string path = ppu == 12 ? "12 px per unit (default)" : $"{ppu} px per unit";
            Menu.SetChecked(MenuRoot + "Art Resolution/" + path, EditorPrefs.GetInt(PpuPrefKey, DefaultPpu) == ppu);
            return true;
        }

        // ---- export driver ----

        // Every exported scene: the World's rooms in order, then the extra scenes. renderArt picks
        // which scenes get their art rendered now; the rest reuse their sidecar from an earlier run.
        private static void Export(Func<string, bool> renderArt, bool saveFirst = true)
        {
            WorldDefinition world = WorldSetup.FindWorld();
            if (world == null)
            {
                EditorUtility.DisplayDialog("Level layout export", "No WorldDefinition asset found.", "OK");
                return;
            }
            if (saveFirst && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            var scenePaths = new List<string>();
            foreach (RoomDefinition room in world.Rooms)
            {
                if (room == null || !room.IsSet) continue;
                string roomPath = AssetDatabase.GetAssetPath(room.Scene.SceneAsset);
                if (!string.IsNullOrEmpty(roomPath) && !scenePaths.Contains(roomPath)) scenePaths.Add(roomPath);
            }
            foreach (string extra in ReadExtraScenes())
                if (!scenePaths.Contains(extra)) scenePaths.Add(extra);

            string originalScene = SceneManager.GetActiveScene().path;
            Directory.CreateDirectory(DataFolder);

            var json = new Json();
            json.BeginObject();
            json.Field("version", 1);
            json.Field("exportedAt", DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture));
            json.Key("scenes").BeginObject();

            var report = new StringBuilder();
            int exported = 0;
            try
            {
                for (int i = 0; i < scenePaths.Count; i++)
                {
                    string path = scenePaths[i];
                    if (!File.Exists(path))
                    {
                        report.AppendLine($"{path}: missing, skipped");
                        continue;
                    }
                    // Same name the runtime (and so the perf log) gives the scene
                    string sceneName = Path.GetFileNameWithoutExtension(path);

                    EditorUtility.DisplayProgressBar("Level layout export", $"Opening {sceneName}…",
                        (float)i / scenePaths.Count);
                    EditorSceneManager.OpenScene(path, OpenSceneMode.Single);

                    SceneLayout layout = Collect(sceneName);
                    if (layout.Tilemaps.Count == 0 && layout.Checkpoints.Count == 0) continue;   // e.g. the End card

                    ArtInfo art = renderArt(path) ? RenderArt(layout, i, scenePaths.Count) : ReadArtSidecar(sceneName);
                    WriteScene(json, layout, art);
                    exported++;
                    report.AppendLine($"{sceneName}: {layout.Checkpoints.Count} checkpoints, {layout.Hazards.Count} hazards, "
                                    + $"{layout.Tilemaps.Count} tilemaps{(art != null ? $", art {art.Width}×{art.Height}px" : "")}");
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                if (!string.IsNullOrEmpty(originalScene)) EditorSceneManager.OpenScene(originalScene, OpenSceneMode.Single);
            }

            json.EndObject();   // scenes
            json.EndObject();

            string text = json.ToString();
            File.WriteAllText(Path.Combine(DataFolder, "level_layouts.json"), text, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(DataFolder, "level_layouts.js"),
                "// Generated by Tools > Inkform > Analytics > Export Level Layouts. Do not edit.\n"
                + "window.INKFORM_LAYOUTS = " + text + ";\n", new UTF8Encoding(false));

            Debug.Log($"[Analytics] Exported {exported} level layouts to {DataFolder}\n{report}");
            EditorUtility.DisplayDialog("Level layout export",
                $"Exported {exported} rooms to\n{DataFolder}\n\n{report}", "OK");
        }

        // ---- collection ----

        private sealed class TilemapData
        {
            public string Name, Kind;
            public int Layer, Width, Height;
            public Vector2 Origin, Cell;
            public byte[] Bits;
            public int Count;
        }

        private sealed class Marker
        {
            public string Name, Kind, Prefab, Cause, SpawnId, Target;
            public Vector2 Position, Spawn;
            public Rect Box;
            public bool StartPoint, Loop, Spin, Timed;
            public List<Vector2> Path;
        }

        private sealed class SceneLayout
        {
            public string Name;
            public Rect Bounds;
            public bool HasBounds;
            public readonly List<TilemapData> Tilemaps = new List<TilemapData>();
            public readonly List<Marker> Checkpoints = new List<Marker>();
            public readonly List<Marker> Doors = new List<Marker>();
            public readonly List<Marker> Hazards = new List<Marker>();
            public readonly List<Marker> Props = new List<Marker>();
            public readonly List<Vector2[]> CamLimits = new List<Vector2[]>();
        }

        private sealed class ArtInfo
        {
            public string File;
            public Rect Rect;
            public int Ppu, Width, Height;
        }

        [Serializable]
        private sealed class ArtSidecar
        {
            public string file;
            public float x, y, w, h;
            public int ppu, width, height;
        }

        private static SceneLayout Collect(string sceneName)
        {
            var layout = new SceneLayout { Name = sceneName };

            foreach (Tilemap tilemap in Object.FindObjectsByType<Tilemap>())
            {
                if (!tilemap.isActiveAndEnabled || IsParallax(tilemap.transform)) continue;
                TilemapData data = ReadTilemap(tilemap);
                if (data == null) continue;
                layout.Tilemaps.Add(data);

                if (data.Kind != "terrain") continue;
                var rect = new Rect(data.Origin, new Vector2(data.Width * data.Cell.x, data.Height * data.Cell.y));
                layout.Bounds = layout.HasBounds ? Union(layout.Bounds, rect) : rect;
                layout.HasBounds = true;
            }
            // Terrain first, then hazards over it, decoration and background last: the viewer draws
            // in reverse so the playable shape always ends on top
            layout.Tilemaps.Sort((a, b) => KindOrder(a.Kind).CompareTo(KindOrder(b.Kind)));

            foreach (Checkpoint checkpoint in Object.FindObjectsByType<Checkpoint>())
            {
                layout.Checkpoints.Add(new Marker
                {
                    Name = checkpoint.name,
                    Position = checkpoint.transform.position,
                    Spawn = checkpoint.SpawnPos,
                    StartPoint = checkpoint.IsStartPoint,
                    SpawnId = checkpoint.SpawnId,
                    Box = WorldBox(checkpoint.gameObject),
                });
            }
            layout.Checkpoints.Sort((a, b) => a.Position.x.CompareTo(b.Position.x));

            foreach (LevelExit exit in Object.FindObjectsByType<LevelExit>())
            {
                layout.Doors.Add(new Marker
                {
                    Name = exit.name,
                    Position = exit.transform.position,
                    Box = WorldBox(exit.gameObject),
                    Target = exit.Destination != null ? exit.Destination.SceneName : "",
                    SpawnId = exit.TargetSpawnId,
                });
            }

            var hazardRoots = new HashSet<GameObject>();
            foreach (HarmOnTouch harm in Object.FindObjectsByType<HarmOnTouch>())
            {
                if (harm.GetComponent<Tilemap>() != null) continue;     // spike tilemaps are tile data
                GameObject root = HazardRoot(harm.gameObject);
                if (!hazardRoots.Add(root)) continue;

                var marker = new Marker
                {
                    Name = root.name,
                    Kind = "hazard",
                    Prefab = PrefabName(root),
                    Cause = CauseOf(harm),
                    Position = root.transform.position,
                    Box = WorldBox(root),
                    Spin = root.GetComponentInChildren<Spinner>(true) != null,
                    Timed = root.GetComponentInChildren<TimedVisibility>(true) != null,
                };
                PatrolMover patrol = root.GetComponentInChildren<PatrolMover>(true);
                if (patrol != null) marker.Path = PatrolPath(patrol, out marker.Loop);
                layout.Hazards.Add(marker);
            }

            CollectProps(layout, hazardRoots);

            foreach (CameraLimit limit in Object.FindObjectsByType<CameraLimit>())
            {
                Vector2 centre = limit.transform.position;
                Vector2 half = (Vector2)limit.transform.up * (limit.Length * 0.5f);
                Vector2 normal = (Vector2)limit.transform.right * (limit.BlockPositive ? 1f : -1f);
                layout.CamLimits.Add(new[] { centre - half, centre + half, normal });
            }

            if (!layout.HasBounds)
            {
                foreach (TilemapData data in layout.Tilemaps)
                {
                    var rect = new Rect(data.Origin, new Vector2(data.Width * data.Cell.x, data.Height * data.Cell.y));
                    layout.Bounds = layout.HasBounds ? Union(layout.Bounds, rect) : rect;
                    layout.HasBounds = true;
                }
            }
            return layout;
        }

        // Interactive pieces worth a marker on the map, keyed by the part that defines them. One entry
        // per Interactable root; hazards were already taken.
        private static void CollectProps(SceneLayout layout, HashSet<GameObject> taken)
        {
            void Add<T>(string kind) where T : Component
            {
                foreach (T part in Object.FindObjectsByType<T>())
                {
                    if (part.GetComponentInParent<Inkform.Player.PlayerHandler>(true) != null) continue;
                    GameObject root = HazardRoot(part.gameObject);
                    if (!taken.Add(root)) continue;
                    var marker = new Marker
                    {
                        Name = root.name,
                        Kind = kind,
                        Prefab = PrefabName(root),
                        Position = root.transform.position,
                        Box = WorldBox(root),
                    };
                    PatrolMover patrol = root.GetComponentInChildren<PatrolMover>(true);
                    if (patrol != null) marker.Path = PatrolPath(patrol, out marker.Loop);
                    layout.Props.Add(marker);
                }
            }

            Add<AbilityPickupPart>("pickup");
            Add<InventoryCapacityPart>("pickup");
            Add<BreakablePart>("breakable");
            Add<Inkform.Item.CrusherMover>("crusher");
            Add<FloatablePart>("floatable");
            Add<CarriablePart>("carriable");
            Add<ExplodePart>("bomb");
            Add<Spawner>("spawner");
            Add<ObjectSpawner>("spawner");
            Add<PatrolMover>("platform");
            Add<TutorialHintPart>("hint");
        }

        private static int KindOrder(string kind) =>
            kind == "terrain" ? 0 : kind == "hazard" ? 1 : kind == "platform" ? 2 : kind == "decor" ? 3 : 4;

        private static TilemapData ReadTilemap(Tilemap tilemap)
        {
            BoundsInt cells = tilemap.cellBounds;
            if (cells.size.x <= 0 || cells.size.y <= 0) return null;

            // Tight extents by scanning every cell: CompressBounds would do it but mutates (and
            // dirties) the scene, and the stored bounds are loose after erasing
            TileBase[] block = tilemap.GetTilesBlock(cells);
            int sx = cells.size.x, sy = cells.size.y, sz = cells.size.z;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            var filled = new bool[sx * sy];
            for (int z = 0; z < sz; z++)
            for (int y = 0; y < sy; y++)
            for (int x = 0; x < sx; x++)
            {
                if (block[x + y * sx + z * sx * sy] == null) continue;
                filled[x + y * sx] = true;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
            if (minX == int.MaxValue) return null;

            int w = maxX - minX + 1, h = maxY - minY + 1;
            var bits = new byte[(w * h + 7) / 8];
            int count = 0;
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (!filled[(x + minX) + (y + minY) * sx]) continue;
                int i = x + y * w;
                bits[i >> 3] |= (byte)(1 << (i & 7));
                count++;
            }

            // Lower-left corner of the first cell and the world size of one cell, both measured through
            // the tilemap itself so per-tilemap offsets (the backgrounds sit at -37,-15.5) come along
            var first = new Vector3Int(cells.xMin + minX, cells.yMin + minY, 0);
            Vector3 origin = tilemap.CellToWorld(first);
            Vector3 corner = tilemap.CellToWorld(first + new Vector3Int(1, 1, 0));

            return new TilemapData
            {
                Name = tilemap.name,
                Kind = TilemapKind(tilemap),
                Layer = tilemap.gameObject.layer,
                Origin = origin,
                Cell = corner - origin,
                Width = w,
                Height = h,
                Bits = bits,
                Count = count,
            };
        }

        private static string TilemapKind(Tilemap tilemap)
        {
            GameObject go = tilemap.gameObject;
            if (go.layer == HazardLayer || go.GetComponent<HarmOnTouch>() != null) return "hazard";
            if (go.GetComponentInParent<PatrolMover>(true) != null) return "platform";

            var collider = go.GetComponent<TilemapCollider2D>();
            bool solid = collider != null && collider.enabled && !collider.isTrigger;
            if (solid && (go.layer == TerrainLayer || go.layer == BreakableLayer)) return "terrain";

            var renderer = go.GetComponent<TilemapRenderer>();
            if (renderer != null && renderer.sortingOrder <= BackgroundSortingOrder) return "background";
            return solid ? "terrain" : "decor";
        }

        private static bool IsParallax(Transform t) =>
            t.GetComponentInParent<ParallaxLayer>(true) != null || t.GetComponentInParent<LocalParallaxZone>(true) != null;

        private static GameObject HazardRoot(GameObject go)
        {
            var root = go.GetComponentInParent<Inkform.Interactable.Interactable>(true);
            return root != null ? root.gameObject : go;
        }

        private static string PrefabName(GameObject go)
        {
            GameObject source = PrefabUtility.GetCorrespondingObjectFromOriginalSource(go);
            return source != null ? source.name : "";
        }

        private static string CauseOf(HarmOnTouch harm)
        {
            SerializedProperty cause = new SerializedObject(harm).FindProperty("cause");
            return cause != null ? ((DeathCause)cause.enumValueIndex).ToString() : DeathCause.Spike.ToString();
        }

        // World-space route, the way PatrolMover's own gizmo draws it: offsets from the placed position,
        // the legacy A/B pair when fewer than two waypoints are set
        private static List<Vector2> PatrolPath(PatrolMover patrol, out bool loop)
        {
            var so = new SerializedObject(patrol);
            var local = new List<Vector2>();
            SerializedProperty waypoints = so.FindProperty("waypoints");
            if (waypoints != null && waypoints.arraySize >= 2)
                for (int i = 0; i < waypoints.arraySize; i++) local.Add(waypoints.GetArrayElementAtIndex(i).vector2Value);
            else
            {
                local.Add(so.FindProperty("pointA")?.vector2Value ?? Vector2.zero);
                local.Add(so.FindProperty("pointB")?.vector2Value ?? Vector2.zero);
            }
            loop = so.FindProperty("mode")?.enumValueIndex == (int)PatrolMover.PatrolMode.Loop;

            Vector2 basePos = patrol.transform.position;
            var world = new List<Vector2>(local.Count);
            foreach (Vector2 point in local) world.Add(basePos + point);
            return world;
        }

        // Bounds of everything that physically or visibly makes up the object. Collider bounds can
        // read zero in edit mode before physics has synced, so boxes are also computed from shape data
        private static Rect WorldBox(GameObject go)
        {
            bool has = false;
            var bounds = new Bounds();
            void Grow(Bounds b)
            {
                if (b.size.sqrMagnitude <= 0f) return;
                if (!has) { bounds = b; has = true; }
                else bounds.Encapsulate(b);
            }

            foreach (Collider2D collider in go.GetComponentsInChildren<Collider2D>())
            {
                if (collider is TilemapCollider2D) continue;
                Bounds b = collider.bounds;
                if (b.size.sqrMagnitude <= 0f && collider is BoxCollider2D box)
                {
                    Vector3 c = box.transform.TransformPoint(box.offset);
                    Vector3 s = Vector3.Scale(box.size, box.transform.lossyScale);
                    b = new Bounds(c, new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), 0.01f));
                }
                else if (b.size.sqrMagnitude <= 0f && collider is CircleCollider2D circle)
                {
                    Vector3 c = circle.transform.TransformPoint(circle.offset);
                    float r = circle.radius * Mathf.Max(Mathf.Abs(circle.transform.lossyScale.x), Mathf.Abs(circle.transform.lossyScale.y));
                    b = new Bounds(c, new Vector3(r * 2f, r * 2f, 0.01f));
                }
                Grow(b);
            }
            if (!has)
                foreach (Renderer renderer in go.GetComponentsInChildren<Renderer>())
                    if (!(renderer is TilemapRenderer)) Grow(renderer.bounds);
            if (!has) return new Rect((Vector2)go.transform.position - Vector2.one * 0.5f, Vector2.one);
            return new Rect(bounds.min, bounds.size);
        }

        private static Rect Union(Rect a, Rect b) =>
            Rect.MinMaxRect(Mathf.Min(a.xMin, b.xMin), Mathf.Min(a.yMin, b.yMin), Mathf.Max(a.xMax, b.xMax), Mathf.Max(a.yMax, b.yMax));

        // ---- art ----

        private static string ArtFileName(string sceneName) => sceneName.Replace(' ', '_') + ".jpg";
        private static string SidecarPath(string sceneName) => Path.Combine(DataFolder, sceneName.Replace(' ', '_') + ".art.json");

        private static ArtInfo ReadArtSidecar(string sceneName)
        {
            string sidecar = SidecarPath(sceneName);
            if (!File.Exists(sidecar)) return null;
            ArtSidecar data = JsonUtility.FromJson<ArtSidecar>(File.ReadAllText(sidecar));
            if (data == null || !File.Exists(Path.Combine(DataFolder, data.file))) return null;
            return new ArtInfo { File = data.file, Rect = new Rect(data.x, data.y, data.w, data.h), Ppu = data.ppu, Width = data.width, Height = data.height };
        }

        private static ArtInfo RenderArt(SceneLayout layout, int roomIndex, int roomCount)
        {
            if (!layout.HasBounds) return null;

            // Two units of margin, snapped to whole units so pixel and world grids line up
            Rect rect = Rect.MinMaxRect(Mathf.Floor(layout.Bounds.xMin) - 2f, Mathf.Floor(layout.Bounds.yMin) - 2f,
                                        Mathf.Ceil(layout.Bounds.xMax) + 2f, Mathf.Ceil(layout.Bounds.yMax) + 2f);
            int ppu = Mathf.Clamp(EditorPrefs.GetInt(PpuPrefKey, DefaultPpu), 4, 32);
            while (ppu > 4 && Mathf.Max(rect.width, rect.height) * ppu > MaxImageEdge) ppu--;
            int width = Mathf.RoundToInt(rect.width * ppu), height = Mathf.RoundToInt(rect.height * ppu);

            var hidden = new List<GameObject>();
            void Hide(GameObject go) { if (go.activeSelf) { go.SetActive(false); hidden.Add(go); } }
            foreach (ParallaxLayer p in Object.FindObjectsByType<ParallaxLayer>()) Hide(p.gameObject);
            foreach (LocalParallaxZone z in Object.FindObjectsByType<LocalParallaxZone>()) Hide(z.gameObject);
            foreach (Inkform.Player.PlayerHandler p in Object.FindObjectsByType<Inkform.Player.PlayerHandler>()) Hide(p.gameObject);
            foreach (Canvas c in Object.FindObjectsByType<Canvas>()) if (c.isRootCanvas) Hide(c.gameObject);

            Color background = new Color(0.06f, 0.06f, 0.08f, 1f);
            foreach (Camera sceneCam in Object.FindObjectsByType<Camera>())
                if (sceneCam.CompareTag("MainCamera")) { background = sceneCam.backgroundColor; background.a = 1f; break; }

            var go = new GameObject("LevelArtCapture") { hideFlags = HideFlags.HideAndDontSave };
            var cam = go.AddComponent<Camera>();
            cam.orthographic = true;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = background;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 1000f;
            cam.enabled = false;

            // sRGB target: in a Linear project the bytes read back are already display-encoded, which is
            // what the JPG wants (a linear target would come out dark)
            var rt = new RenderTexture(ChunkPixels, ChunkPixels, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                { hideFlags = HideFlags.HideAndDontSave };
            var image = new Texture2D(width, height, TextureFormat.RGB24, false, false);
            var chunk = new Texture2D(ChunkPixels, ChunkPixels, TextureFormat.RGB24, false, false);
            float chunkWorld = (float)ChunkPixels / ppu;
            int cols = Mathf.CeilToInt(width / (float)ChunkPixels), rows = Mathf.CeilToInt(height / (float)ChunkPixels);

            RenderTexture previous = RenderTexture.active;
            try
            {
                cam.orthographicSize = chunkWorld * 0.5f;
                cam.aspect = 1f;
                for (int row = 0; row < rows; row++)
                for (int col = 0; col < cols; col++)
                {
                    EditorUtility.DisplayProgressBar("Level layout export",
                        $"Rendering {layout.Name} art ({row * cols + col + 1}/{rows * cols})…",
                        (roomIndex + (row * cols + col) / (float)(rows * cols)) / roomCount);

                    Vector2 centre = new Vector2(rect.xMin + (col + 0.5f) * chunkWorld, rect.yMin + (row + 0.5f) * chunkWorld);
                    go.transform.position = new Vector3(centre.x, centre.y, -100f);
                    RenderChunk(cam, rt);

                    RenderTexture.active = rt;
                    chunk.ReadPixels(new Rect(0, 0, ChunkPixels, ChunkPixels), 0, 0, false);
                    chunk.Apply(false);

                    int px = col * ChunkPixels, py = row * ChunkPixels;
                    int cw = Mathf.Min(ChunkPixels, width - px), ch = Mathf.Min(ChunkPixels, height - py);
                    image.SetPixels(px, py, cw, ch, chunk.GetPixels(0, 0, cw, ch));
                }
                image.Apply(false);

                string file = ArtFileName(layout.Name);
                File.WriteAllBytes(Path.Combine(DataFolder, file), image.EncodeToJPG(JpgQuality));
                var info = new ArtInfo { File = file, Rect = rect, Ppu = ppu, Width = width, Height = height };
                File.WriteAllText(SidecarPath(layout.Name), JsonUtility.ToJson(new ArtSidecar
                {
                    file = file, x = rect.x, y = rect.y, w = rect.width, h = rect.height, ppu = ppu, width = width, height = height,
                }));
                return info;
            }
            finally
            {
                RenderTexture.active = previous;
                cam.targetTexture = null;
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(image);
                Object.DestroyImmediate(chunk);
                foreach (GameObject h in hidden) if (h != null) h.SetActive(true);
            }
        }

        // SRP render requests are the supported way to render a camera on demand under URP; the
        // legacy Render() call stays as the fallback for pipelines that refuse the request
        private static void RenderChunk(Camera cam, RenderTexture rt)
        {
            var request = new RenderPipeline.StandardRequest { destination = rt };
            if (RenderPipeline.SupportsRenderRequest(cam, request))
            {
                RenderPipeline.SubmitRenderRequest(cam, request);
                return;
            }
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = null;
        }

        // ---- json ----

        private static void WriteScene(Json json, SceneLayout layout, ArtInfo art)
        {
            json.Key(layout.Name).BeginObject();
            json.Field("name", layout.Name);
            json.Key("bounds").Numbers(layout.Bounds.xMin, layout.Bounds.yMin, layout.Bounds.xMax, layout.Bounds.yMax);

            json.Key("tilemaps").BeginArray();
            foreach (TilemapData t in layout.Tilemaps)
            {
                json.NewLine().BeginObject();
                json.Field("name", t.Name);
                json.Field("kind", t.Kind);
                json.Field("layer", t.Layer);
                json.Key("origin").Numbers(t.Origin.x, t.Origin.y);
                json.Key("cell").Numbers(t.Cell.x, t.Cell.y);
                json.Field("w", t.Width);
                json.Field("h", t.Height);
                json.Field("count", t.Count);
                json.Field("bits", Convert.ToBase64String(t.Bits));
                json.EndObject();
            }
            json.EndArray();

            json.Key("checkpoints").BeginArray();
            foreach (Marker m in layout.Checkpoints)
            {
                json.NewLine().BeginObject();
                json.Field("name", m.Name);
                json.Key("pos").Numbers(m.Position.x, m.Position.y);
                json.Key("spawn").Numbers(m.Spawn.x, m.Spawn.y);
                json.Key("box").Box(m.Box);
                if (m.StartPoint) json.Field("start", true);
                if (!string.IsNullOrEmpty(m.SpawnId)) json.Field("spawnId", m.SpawnId);
                json.EndObject();
            }
            json.EndArray();

            json.Key("doors").BeginArray();
            foreach (Marker m in layout.Doors)
            {
                json.NewLine().BeginObject();
                json.Field("name", m.Name);
                json.Key("pos").Numbers(m.Position.x, m.Position.y);
                json.Key("box").Box(m.Box);
                json.Field("to", m.Target);
                if (!string.IsNullOrEmpty(m.SpawnId)) json.Field("spawnId", m.SpawnId);
                json.EndObject();
            }
            json.EndArray();

            WriteMarkers(json, "hazards", layout.Hazards);
            WriteMarkers(json, "props", layout.Props);

            json.Key("camLimits").BeginArray();
            foreach (Vector2[] l in layout.CamLimits)
            {
                json.NewLine().BeginObject();
                json.Key("a").Numbers(l[0].x, l[0].y);
                json.Key("b").Numbers(l[1].x, l[1].y);
                json.Key("n").Numbers(l[2].x, l[2].y);
                json.EndObject();
            }
            json.EndArray();

            if (art != null)
            {
                json.Key("art").BeginObject();
                json.Field("file", art.File);
                json.Key("rect").Numbers(art.Rect.x, art.Rect.y, art.Rect.width, art.Rect.height);
                json.Field("ppu", art.Ppu);
                json.Field("width", art.Width);
                json.Field("height", art.Height);
                json.EndObject();
            }
            json.EndObject();
        }

        private static void WriteMarkers(Json json, string key, List<Marker> markers)
        {
            json.Key(key).BeginArray();
            foreach (Marker m in markers)
            {
                json.NewLine().BeginObject();
                json.Field("name", m.Name);
                json.Field("kind", m.Kind);
                if (!string.IsNullOrEmpty(m.Prefab)) json.Field("prefab", m.Prefab);
                if (!string.IsNullOrEmpty(m.Cause)) json.Field("cause", m.Cause);
                json.Key("pos").Numbers(m.Position.x, m.Position.y);
                json.Key("box").Box(m.Box);
                if (m.Spin) json.Field("spin", true);
                if (m.Timed) json.Field("timed", true);
                if (m.Path != null && m.Path.Count >= 2)
                {
                    json.Key("path").BeginArray();
                    foreach (Vector2 p in m.Path) json.Numbers(p.x, p.y);
                    json.EndArray();
                    if (m.Loop) json.Field("loop", true);
                }
                json.EndObject();
            }
            json.EndArray();
        }

        /// <summary>Minimal JSON writer: compact, one array element per line, invariant numbers.
        /// JsonUtility cannot write dictionaries keyed by scene name, which is the shape the viewer wants.</summary>
        private sealed class Json
        {
            private readonly StringBuilder sb = new StringBuilder(1 << 20);
            private bool needComma;

            public Json Key(string key) { Comma(); Str(key); sb.Append(':'); needComma = false; return this; }
            public Json BeginObject() { Comma(); sb.Append('{'); needComma = false; return this; }
            public Json EndObject() { sb.Append('}'); needComma = true; return this; }
            public Json BeginArray() { Comma(); sb.Append('['); needComma = false; return this; }
            public Json EndArray() { sb.Append(']'); needComma = true; return this; }
            public Json NewLine() { if (needComma) { sb.Append(','); needComma = false; } sb.Append('\n'); return this; }

            public void Field(string key, string value) { Key(key); Str(value ?? ""); needComma = true; }
            public void Field(string key, int value) { Key(key); sb.Append(value.ToString(CultureInfo.InvariantCulture)); needComma = true; }
            public void Field(string key, bool value) { Key(key); sb.Append(value ? "true" : "false"); needComma = true; }

            public Json Numbers(params float[] values)
            {
                Comma();
                sb.Append('[');
                for (int i = 0; i < values.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(Math.Round(values[i], 3).ToString(CultureInfo.InvariantCulture));
                }
                sb.Append(']');
                needComma = true;
                return this;
            }

            public Json Box(Rect r) => Numbers(r.xMin, r.yMin, r.xMax, r.yMax);

            private void Comma()
            {
                if (needComma) sb.Append(',');
                needComma = false;
            }

            private void Str(string s)
            {
                sb.Append('"');
                foreach (char c in s)
                {
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                            else sb.Append(c);
                            break;
                    }
                }
                sb.Append('"');
            }

            public override string ToString() => sb.ToString();
        }
    }
}

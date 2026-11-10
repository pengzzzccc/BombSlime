# PerfLog Viewer

A standalone static web tool for visualising playtest telemetry: drag and drop `perf_*.csv` files recorded by `PerformanceRecorder` to inspect player deaths, movement trajectories, per-checkpoint segment difficulty, and frame-time performance directly over actual level art. No installation, build step, or internet connection is required—all data stays on your local machine.

## Usage

1. Enable recording in-game under **Video › Perf Recording**. Set **Perf Rate** to `0.5 s` or `0.1 s` for finer movement trajectories. Logs are written to `Docs/PerfLogs/`.
2. In the Unity Editor, run **Tools › Inkform › Analytics › Export Level Layouts + Art**.
   - This generates `data/level_layouts.js` along with a rendered `*.jpg` screenshot for each level.
   - If you only need level geometry, use the variant without `+ Art` for faster export.
   - Adjust the rendered image resolution under the **Art Resolution** submenu (defaults to `12 px/tile`).
   - The menu items above only export rooms registered in the `World` asset. For scenes outside the World registry (such as `Lab_base`), open the scene in Unity first and run **Export Current Scene + Art**:
     - It renders only the active scene's art while preserving previously exported images for other scenes.
     - The scene path is recorded in `data/extra_scenes.txt` so subsequent exports automatically include it.
     - To stop exporting an extra scene, remove its line from `data/extra_scenes.txt` and delete its `*.jpg` and `*.art.json` files.
3. Double-click `index.html` (or run **Tools › Inkform › Analytics › Open PerfLog Viewer** in Unity), then drag and drop one or more CSV files into the page.

## Views

| Tab | Contents |
|---|---|
| **Level Map** | Three base map modes (*Art*, *Schematic*, *Hybrid*) with toggleable overlays: **Death Heatmap**, **Lethality Rate** (deaths ÷ lives passing through), **Dwell Heatmap**, **Performance Spikes**, **Mechanic Usage** (wall slide, ceiling stick, rope gun, airborne), **Unreached Areas**, **Death Contours**, **Player Trajectories**, **Segment Difficulty Arcs**, **Checkpoints**, **Top Hazard Sources**, and **Unstuck Events** (where `UnstuckSolver` intervened: amber = nudged to a nearby safe spot, red = teleported back to checkpoint). Includes **Ghost Replay** and PNG export with legend. |
| **Overview** | High-level KPIs, per-scene playtime and death totals, and the session table. |
| **Checkpoint Segments** | Attempt count, deaths, first-try clear rate, median duration, and difficulty rating per segment. Clicking a row focuses the corresponding segment on the map. Also includes a **Segment × Session** matrix. |
| **Death Analysis** | Breakdown by death cause, Top 10 lethal hazards, player state at death, survival-time distribution, and respawn-trap detection. |
| **Difficulty Curve** | Interactive version of `difficulty_curve.py`, toggleable between normalised progress and actual elapsed time. |
| **Performance** | Synchronised zoom charts for **FPS**, **Frame Time** (including spike peaks), and **GC Allocations**, plus per-scene frame-time box plots and rendering statistics. |

### Map Controls

| Input | Action |
|---|---|
| Mouse Wheel | Zoom in / out |
| Click & Drag | Pan |
| Double-click | Focus on the highest-density death cluster |
| `0` | Fit level to view |
| `+` / `-` | Zoom in / out |
| Arrow Keys | Pan |
| `Space` (during replay) | Play / Pause ghost replay |
| Hover session in sidebar | Highlight only that session on the map |

## Log Format (`format=2`)

Periodic sample rows remain unchanged. Gameplay events are written as comment lines prefixed with `#`, keeping backwards compatibility with older tools (including `difficulty_curve.py`):

```csv
#E-schema,death,time_s,play_s,scene,x,y,hit_x,hit_y,cause,source,state,rope,vx,vy,life_s,seg,seg_deaths
#E-schema,respawn,time_s,play_s,scene,x,y,seg
#E-schema,checkpoint,time_s,play_s,scene,x,y,name,seg
#E-schema,segment,time_s,play_s,scene,seg,from,from_x,from_y,to,to_x,to_y,start_s,duration_s,play_dur_s,deaths,end_reason
```

- Always read fields by their column name in `#E-schema` rather than by fixed index.
- `play_s`: accumulated time spent exclusively in the `Playing` state (excludes pauses and cutscenes).
- `segment`: a run between two checkpoints. `end_reason` is one of `checkpoint`, `scene`, or `stop`.

Legacy logs (`format=1`, without `#E` event lines) are still supported:

- Deaths are inferred from increments in the `deaths` column, using the previous sample's coordinates.
- When level layout data is available, segments are inferred by intersecting player trajectories with checkpoint trigger bounds.

Both types of inferred records are badged as **Inferred** in the UI.

## In-Editor Scene Overlay

Running **Tools › Inkform › Analytics › PerfLog Scene Overlay** in Unity draws the same death markers, heatmaps, and player trajectories directly over the live level art inside the Unity **Scene View**, sharing the exact colour palette and iconography of the web viewer.

## File Structure

```text
index.html              Main application entry point
css/viewer.css          Stylesheet
js/i18n.js              Bilingual UI strings (EN / ZH)
js/theme.js             Design tokens → Canvas / ECharts colour palette (single source of truth)
js/parse.js             CSV and event-schema parser
js/model.js             Session, scene aggregation, segment, and route ordering model
js/heat.js              Heatmap fields, lethality rate, coverage, and contour calculation
js/mapview.js           Canvas map renderer, overlays, ghost replay, and PNG export
js/charts.js            ECharts card builders
js/app.js               UI state and interaction wiring
vendor/echarts.min.js   Bundled ECharts 5.5.1 (Apache-2.0) for offline use
data/                   Exported room layouts and rendered level art
```

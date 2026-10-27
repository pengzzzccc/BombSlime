# Inkform

A 2D platformer built with Unity. You play a slime that swallows bombs and spits them back out
to set off chain explosions. You pull yourself across gaps with a rope gun, dash through walls,
slide down walls and stick to ceilings. Levels are built from breakable walls, hazards and
checkpoints. When you die, the level itself rewinds as well as the player.

COMP3151 course project. Contributors: pengzzzccc, MQ-AndyZhan, 48027480, Viet Phi Long Le, Julian-Baxter.

## Gameplay Features

- **Swallow & spit bombs**: reel a bomb in with the rope gun to swallow it, then spit it out.
  A spat bomb is *armed* and goes off when it hits terrain or a breakable. Bombs caught in a blast
  chain-detonate in sequence; per bomb, this can be turned off with `chainReaction`.
- **Floating bombs**: zero-gravity bombs that blasts and the player can knock around in 8 directions.
- **Rope gun**: grapple terrain and pull yourself across gaps, with a free-aim reticle
  (mouse or right stick).
- **Movement**: dash (which smashes breakables in its path), wall slide, wall jump, ceiling stick
  and ceiling crawl, plus coyote time.
- **Hazards**: spikes, crushers, fire jets, lasers, patrolling movers, spinners, and hazards that
  blink on and off on a timer.
- **Checkpoints with state restore**: dying resets the level as well as the player, so shattered
  walls and spent bombs come back and a puzzle can always be retried fresh.
- **Progression**: backpack capacity pickups that persist in the save, a map wall that shows the
  room layout, and tutorial hint zones.
- **Unstuck**: if the player is wedged inside geometry, a solver finds a nearby safe spot.
- **Game feel**: screen shake, hit stop, camera zoom punch, shatter fragments, blast waves and
  parallax backgrounds.
- **Haptics**: gamepad rumble, plus DualSense adaptive-trigger effects.
- **Full settings**: audio, video, rebindable controls, input device selection, rumble and
  trigger strength, and save slots.

## Controls

| Action | Keyboard & Mouse | Gamepad |
|---|---|---|
| Move | WASD / Arrow keys | Left stick / D-pad |
| Aim | Mouse | Right stick |
| Jump | Space | Left trigger (LT) |
| Dash | Left Shift | Left shoulder (LB) |
| Rope fire | Left mouse button | Right trigger (RT) |
| Spit bomb | Q | Right shoulder (RB) |
| Interact | E | Y (north) |
| Crouch | C | B (east) |
| Previous / next item | 1 / 2 | D-pad left / right |
| Pause | Esc | Start |

Gameplay bindings can be remapped in **Settings → Controls**. Overrides are stored in PlayerPrefs
under `Inkform.bindingOverrides.v4`. With a gamepad, menus use focus navigation: the left stick or
D-pad moves the focus, A confirms and B goes back. The source of truth for bindings is
`Assets/Code/Input/InputSystem_Actions.inputactions`. Pause is hard-wired in `UIManager`.

## Requirements

| | |
|---|---|
| Unity | **6000.4.10f1**. This must match exactly: opening the project in another version auto-upgrades scenes and prefabs and floods the diff. |
| Render pipeline | URP 17.4, 2D Renderer |
| Input | Unity Input System 1.19 |
| UI | UI Toolkit (uGUI 2.0 is only used by legacy pieces) |
| Tests | Unity Test Framework 1.6 |

## Getting Started

1. In Unity Hub, click **Add** and select the `Inform_comp3151/` folder. This is the Unity project
   inside the repository, **not** the repository root.
2. The first asset import takes a few minutes.
3. Open `Assets/Scenes/Menu/MainMenu.unity` and press Play. **New Game** loads the entry room
   defined by the world asset, which is currently *Mine Cave 1*.
4. You can also press Play in any level scene directly. The `GameManager` prefab boots in every
   scene and survives scene loads (`PersistentGameRoot`).

## Project Structure

```
Inform_comp3151/
├── Assets/
│   ├── Animation/   Animation clips and state machines
│   ├── Art/         Sprites · materials · tiles · UI
│   ├── Audio/       Audio clips, SoundCue assets, Inkform.mixer
│   ├── Code/        Runtime scripts, namespace Inkform.<Folder>
│   ├── Editor/      Editor tooling + EditMode tests (*Tests.cs)
│   ├── Fx/          FX data assets (fragment cues, …)
│   ├── Life/        Death strategy assets
│   ├── Physics/     Physics materials
│   ├── Prefabs/     Control/ (GameManager, Player, camera) · Item/ · LongPrefabs/ (bombs, spawners) · env/
│   ├── Scenes/      Menu/ · Level1/ · Level2/ · Test/
│   └── Settings/    URP config · Build Profiles/ · World/ (World.asset + Rooms/)
└── Docs/            Design docs, PerfLogs/, Tool/PerfLogViewer (web viewer)
```

`Assets/Code/` by domain:

| Folder | Contents |
|---|---|
| `Ability/` | Unlockable ability ids + `AbilityStore` (saved per slot) |
| `Audio/` | `AudioService`, `AudioDirector`, `SoundCue`, `AmbientSource`, music fading |
| `Bus/` | Static event buses + `GameStateStore` |
| `Fx/` | `FxDirector`, camera (`CamHandler`, `CameraLimit`), hit stop, shatter/fragments, parallax; `Haptics/` (rumble, DualSense triggers) |
| `Input/` | `InputHandler`, rebinding (`BindingTools`), generated action classes |
| `Interactable/` | `Interactable` node + `Parts/` (see below) |
| `Item/` | `InventoryStore`, item definitions, `CrusherMover` |
| `Level/` | `SceneDirector`, `SceneFader`, `WorldDefinition`/`RoomDefinition`, `LevelExit`, `Checkpoint`, `RespawnDirector`, `UnstuckSolver` |
| `Life/` | `DeathDirector`, death strategies (burn / crush / shatter), `LevelMemento` |
| `Player/` | `PlayerHandler`, `PlayerMotor`, `ContactSensor`, `RopeGun`, `PlayerInventory`, animation state |
| `Save/` | `SaveData`, `SaveStore` |
| `Settings/` | `SettingsStore` (PlayerPrefs) |
| `Tool/` | `PerformanceRecorder`, `DebugCheats`, timers, tags, helpers |
| `UI/` | `UIManager` + `Toolkit/` (HUD, menus, settings panels) |

## Architecture

### Event buses
Eight static buses decouple gameplay from presentation: `PlayerBus`, `LifeBus`, `ItemBus`,
`HazardBus`, `FxBus`, `RopeGunBus`, `LevelBus` and `UiBus`. Gameplay code raises events, and
*director* classes turn them into effects:

| Director | Listens to | Does |
|---|---|---|
| `FxDirector` | `HazardBus.Blast` | Raises `FxBus` shake / hit stop / zoom (consumed by `CamHandler`, `GameTimeController`, `ScreenFx`) |
| `AudioDirector` | Hazard, Item, Life, Player, RopeGun | Plays sound cues |
| `HapticsDirector` | Hazard, Item, Life, Player, RopeGun, Ui | Rumble + DualSense trigger effects |
| `DeathDirector` | `LifeBus.Died` | Runs the death strategy for the cause |
| `RespawnDirector` | `LifeBus` | Respawns at the last checkpoint |
| `SceneDirector` | `LevelBus.ExitReached` | Room transitions |

### World and scene flow
`SceneDirector` is the only component that changes scenes. It reads a `WorldDefinition`
(`Assets/Settings/World/World.asset`): the menu scene, the entry room (Mine Cave 1), the end room
(End) and the registry of `RoomDefinition` assets in `World/Rooms/`.

- Room-to-room topology lives on the doors. Each `LevelExit` references its destination
  `RoomDefinition` and an arrival `Checkpoint.spawnId`; an empty id means the room's start point.
  There is no central graph file.
- A transition fades out with the `SceneFader` curtain, loads the next scene asynchronously, waits
  for frames to settle, then fades back in. Music fades along with it.
- `Assets/Scenes/LevelGraph.txt` is legacy. It is only read by the
  *Migrate From LevelGraph.txt* menu item.

### Interactable framework
Every hazard, pickup and destructible is an `Interactable` node built from interchangeable
parts (`Assets/Code/Interactable/Parts/`):

| Part | Role |
|---|---|
| `ExplodePart` | Bomb: detonates on touching the player, touching a hazard, hitting the world once armed, or in a chain |
| `BreakablePart` | Shatters when caught in a blast |
| `CarriablePart` | Can be swallowed after a rope pull and spat back out |
| `FloatablePart` | Zero gravity; knocked around by blasts and contact |
| `RestorablePart` | Rolls the node back on respawn (with any `IRestorablePart` on it) |
| `HarmOnTouch` | Kills on contact |
| `SolidSurface` | Standable surface |
| `Spinner` / `PatrolMover` | Constant rotation / waypoint movement |
| `TimedVisibility` | Cycles on and off |
| `InteractionPromptPart` | Outline glow + Interact key prompt |
| `InventoryCapacityPart` | Permanent backpack capacity pickup |
| `MapViewPart` | Freezes play and pans the camera to a map wall |
| `TutorialHintPart` | Shows a tutorial hint in its zone |
| `AbilityPickupPart` | Retired ability card (kept only for Mine Cave 2) |

### Checkpoint restore
`LevelMemento` (on the GameManager) collects every `IRestorable` in the scene. It captures their
state on scene entry and on each `LifeBus.CheckpointSet`, and restores it on `LifeBus.Respawned`.
This is what brings shattered walls back after a death. The level state lives only for the
session and is not written to the save file.

## Audio

Audio uses Unity's own pipeline, limited to what the WebGL backend supports, so desktop and
WebGL builds sound the same: `AudioSource` volume/pitch/3D linear rolloff, `AudioMixer` group
volumes, and `AudioListener` pause/volume. There are no audio filters, mixer effects or `panStereo`.

- **`AudioService`** (on the GameManager) is the front door. Call `AudioService.Play(cue, position)`
  for one-shots and `PlayMusic` / `StopMusic` for the crossfading music bed. All entry points are
  null-safe. One-shots use a fixed set of voices, and each `SoundCue` has a cooldown and a
  concurrency cap. When every voice is busy, a new sound takes over the oldest voice in the same
  or a less important `priority` lane, or is dropped.
- **Mixer**: `Assets/Audio/Inkform.mixer` routes `Master` → `Music` and `Sfx`. The settings
  sliders drive its exposed `MasterVolume`, `MusicVolume` and `SfxVolume` parameters.
- **Spatial cues** use Unity's 3D audio. Emitters sit on the world plane (z = 0) and the
  AudioListener rides the camera at z = -10. The linear rolloff makes a sound full volume under the
  camera and silent at the cue's `falloffRange` (`AudioSpatial`).
- **Looping world sounds** are `AmbientSource` components. A `TimedVisibility` hazard can gate one,
  so its loop is audible exactly while the hazard is visible.
- **WebGL caveat**: browsers block audio until the first click or key press, so the page has to be
  interacted with before music starts.

## Saves & Settings

- **Saves**: JSON files `slot0.json` to `slot2.json` (plus a `.bak` each) in a `Saves/` folder next
  to the executable. In the editor, that folder is the `Inform_comp3151/` project root.
  `SaveData` (v4) stores the scene, spawn point, play time, deaths, inventory, capacity pickups
  and unlocked abilities.
- **Settings**: stored in PlayerPrefs under `Inkform.*` keys. They cover volume, sensitivity,
  input device, rebinds, resolution, fullscreen, FPS cap, vsync, FX intensity, rumble and trigger
  strength, rope snapping, and perf recording.

## Editor Tools

| Menu | What it does |
|---|---|
| `Tools > Inkform > World > Validate World (quick / deep)` | Checks rooms, doors and spawn ids; *deep* opens every scene |
| `Tools > Inkform > World > Create Missing Doors & Spawns` | Scaffolds doors and checkpoints the world references |
| `Tools > Inkform > World > Sync Build Settings` | Rebuilds the build list from the world asset |
| `Tools > Inkform > World > Migrate From LevelGraph.txt` | One-off migration from the legacy graph |
| `Tools > Inkform > Analytics > Export Level Layouts (+ Art)` | Exports room layouts for the PerfLog viewer |
| `Tools > Inkform > Analytics > Open PerfLog Viewer` | Opens `Docs/Tool/PerfLogViewer` |
| `Tools > Inkform > Analytics > PerfLog Scene Overlay` | Draws recorded deaths, paths and difficulty in the Scene view |
| `Tools > Inkform > UIToolkit Bootstrap` | One-time UI Toolkit asset setup |
| `Tools > Player > Build Animations` | Builds player clips from the sprite sheets into `Player.controller` |

**Editor-only cheats** (`DebugCheats`, never compiled into builds): **F1** infinite bombs,
**F2** flight, **F3** invincibility, **F5** +1 backpack capacity.

## Playtest Analytics

When **Perf recording** is enabled in Settings, `PerformanceRecorder` writes
`perf_yyyyMMdd_HHmmss.csv`. The file goes to a `Log/` folder at the project root if that folder
exists, otherwise to `Docs/PerfLogs/`. Each log is stamped with the git branch and commit, and
holds periodic samples plus gameplay events and per-checkpoint segment summaries. There are three
ways to analyse the logs:

- the in-editor Scene overlay
- the web viewer in `Docs/Tool/PerfLogViewer/`
- `Docs/PerfLogs/difficulty_curve.py`, which plots difficulty curves

## Tests

EditMode tests live in `Assets/Editor/*Tests.cs`. They cover abilities and prompts, the bomb slot
HUD, burn-away FX, haptics, audio, `ExplodePart`, runtime edge cases, parallax, perf-log segments,
tutorial hints, UI input, the unstuck solver and the world asset. To run them, open
**Window > General > Test Runner**, choose **EditMode** and click **Run All**.

## Scenes

| Scene | Purpose |
|---|---|
| `Scenes/Menu/MainMenu.unity` | Main menu, the first scene in the build |
| `Scenes/Level1/Mine Cave 1.unity` | Entry room, where New Game starts |
| `Scenes/Level1/Mine Cave 2.unity`, `Mine Cave 3.unity` | The rest of the Mine Cave loop, linked by doors |
| `Scenes/Level2/Lab basement.unity` | Level 2 room (registered as `Lab_base`) |
| `Scenes/Menu/End.unity` | End room / credits |
| `Scenes/Level1/hIde_path.unity`, `Scenes/Level2/Lab_build.unity` | Work-in-progress rooms, not in the world registry |
| `Scenes/Test/*` | Debug scenes: `PlayerTest`, `BombDash`, `AnimationTest`, `TileMapTest`, `Julian's Levels/Player_test` |

## Building

Build profiles live in `Assets/Settings/Build Profiles/`: **Windows** (the main target,
1920×1080), **Web** (WebGL) and **Android**.

> Build Settings are derived state. Before building, run
> **Tools > Inkform > World > Sync Build Settings**, which puts the menu first and every
> registered room after it. The committed `EditorBuildSettings.asset` is currently stale, so run
> the sync first.

## Important Notes

- **`.meta` files must move with their asset.** Unity resolves references by the GUID stored in
  `.meta`. Leaving a `.meta` behind turns every scene or prefab that references the asset into a
  Missing reference, and no error appears until the scene is opened. Move or rename assets in the
  Unity Project window, or `git mv` both the asset and its `.meta`.
- **Keep the Unity version pinned to 6000.4.10f1.** Upgrading scenes or prefabs in another version
  silently rewrites files and floods the diff.
- `Build/`, `Saves/`, `Library/`, `Logs/` and the generated `*.csproj` files are gitignored. The
  `Inkform.LevelGraph.*.csproj` files you may see locally are stale leftovers of a removed tool and
  can be ignored.

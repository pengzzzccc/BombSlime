using System;
using System.Collections.Generic;
using UnityEngine;

namespace Inkform.Settings
{
    /// <summary>
    /// Game settings: the single source of truth for player preferences (volumes, sensitivities,
    /// graphics options), persisted in PlayerPrefs.
    ///
    /// Fully static — no scene object, no prefab wiring: values load automatically before the first
    /// scene loads (RuntimeInitializeOnLoadMethod) and every Set saves back. Readers use the static
    /// properties directly: AudioService writes the volumes to the mixer and subscribes to Changed
    /// to follow slider edits, RopeGun applies sensitivity and subscribes to Changed to re-read
    /// after edits. The Settings panel is the main writer; InputHandler and UIManager also flip
    /// Device when they detect the active input family.
    ///
    /// Static (over a GameManager component) because settings are cross-domain infrastructure like the
    /// buses — Audio / Player / UI all touch them — and a static API needs zero scene or prefab edits.
    /// Same lifecycle pattern as the buses: ResetStatics handles Domain Reload off, the
    /// RuntimeInitializeOnLoadMethod loads persisted values (a plain static default would be wiped by
    /// the ResetStatics pass that must run for the event).
    /// </summary>
    /// <summary>Gamepad rumble strength. Each pad family maps the levels onto its own motors:
    /// HapticsDirector's table for the DualSense, GenericPadHaptics' shares for every other pad.</summary>
    public enum RumbleLevel { Off, Low, Medium, High }

    public static class SettingsStore
    {
        /// <summary>Which input device family gameplay input is filtered to (InputHandler).</summary>
        public enum InputDevice { KeyboardMouse, Gamepad }

        // Sensitivity is a plain multiplier, shown as such in the UI (e.g. "2.5"); 1.0 means no change.
        public const float MinSensitivity = 0f;
        public const float MaxSensitivity = 5f;

        // Defaults (first run, and RESET ALL). Rumble and trigger effects share one default level.
        public const RumbleLevel DefaultLevel = RumbleLevel.Medium;
        public const float DefaultStickSensitivity = 1.6f;
        public const float DefaultRopeSnapDeadZone = 0.25f;

        // ---- Values (PlayerPrefs-backed) ----

        public static float MasterVolume { get; private set; } = 1f;
        public static float MusicVolume { get; private set; } = 1f;
        public static float SfxVolume { get; private set; } = 1f;
        public static float MouseSensitivity { get; private set; } = 1f;
        public static float StickSensitivity { get; private set; } = DefaultStickSensitivity;

        /// <summary>Silences everything without touching the three volume values, so unmuting restores
        /// the mix the player set. Applied through AudioListener.volume — see ApplyAudio.</summary>
        public static bool Muted { get; private set; }

        /// <summary>Input family gameplay input is filtered to (InputHandler).</summary>
        public static InputDevice Device { get; private set; } = InputDevice.KeyboardMouse;

        public static int ResolutionWidth { get; private set; }
        public static int ResolutionHeight { get; private set; }
        public static bool Fullscreen { get; private set; } = true;
        public static int FpsCap { get; private set; } = 120;     // 0 = uncapped
        public static bool VSync { get; private set; }

        /// <summary>Frame rate counter overlay visible in gameplay.</summary>
        public static bool ShowFps { get; private set; }

        /// <summary>The run timer at the HUD's top left. Hiding it only hides it: the run's
        /// clock (SaveStore.PlaySeconds) keeps counting.</summary>
        public static bool ShowTimer { get; private set; } = true;

        /// <summary>Session performance recorder on/off — writes the Docs/PerfLogs (or Log/) CSV
        /// traces. Off by default: it is a development tool, and a player's session should not pay
        /// for a CSV nobody asked for.</summary>
        public static bool PerfRecording { get; private set; } = false;

        /// <summary>Performance recorder sampling interval in seconds (0.1 = 10 Hz). Values outside
        /// PerfIntervals never get in through the setter.</summary>
        public static float PerfInterval { get; private set; } = 1f;

        /// <summary>Gamepad rumble strength (the motors); HapticsDirector and GenericPadHaptics
        /// read it live. Off stops the motors only — the DualSense's trigger feel has a level of
        /// its own (TriggerLevel).</summary>
        public static RumbleLevel RumbleLevel { get; private set; } = DefaultLevel;

        /// <summary>Any rumble at all (level above Off).</summary>
        public static bool Rumble => RumbleLevel != RumbleLevel.Off;

        /// <summary>Trigger feel strength: the DualSense's adaptive-trigger resistance, snap-back
        /// and buzz at Low / Medium / High, or Off (other pads have no trigger feel).
        /// HapticsDirector reads this live, independently of the rumble level.</summary>
        public static RumbleLevel TriggerLevel { get; private set; } = DefaultLevel;

        /// <summary>Any trigger feel at all (level above Off).</summary>
        public static bool TriggerEffects => TriggerLevel != RumbleLevel.Off;

        // ---- Grapping hook (rope gun) aim feel; RopeGun reads these live ----

        /// <summary>Reticle snaps to the terrain anchor while green. Off: the green reticle rides
        /// the free cursor (color/firing unchanged).</summary>
        public static bool RopeWallSnap { get; private set; } = true;

        /// <summary>Carriables (bombs) on the aim path are snap targets and eat-pull candidates.
        /// Off: the rope ignores them entirely — preview never snaps, firing never eat-pulls.</summary>
        public static bool RopeBombSnap { get; private set; } = true;

        /// <summary>Snap escape dead zone in world units: the free cursor must pull closer than
        /// the snap target by this margin before the snap releases. Prevents boundary flicker.</summary>
        public static float RopeSnapDeadZone { get; private set; } = 0.3f;

        /// <summary>While sliding on a wall snap, rescale the aim input by the measured geometric
        /// gain so the anchor slides at the free-cursor speed the sensitivity settings define,
        /// whatever the wall angle. Not a setting of its own: it goes with Wall Snap (the slide
        /// it corrects only exists while wall snapping).</summary>
        public static bool RopeAdaptiveSpeed => RopeWallSnap;

        /// <summary>Global visual FX intensity 0~1: scales screen shake, camera zoom, post punch and
        /// shatter launch speed at their consumers. Hitstop is a duration, not a strength, so it is
        /// deliberately untouched — same for gamepad rumble, which is haptic, not visual.</summary>
        public static float FxIntensity { get; private set; } = 1f;

        /// <summary>Frame cap options offered by the Video page; 0 = uncapped. The current pick
        /// lives in FpsCap; this list only feeds the UI's left/right stepping.</summary>
        public static readonly int[] FpsOptions = { 30, 60, 120, 0 };

        /// <summary>Performance recorder sampling intervals (seconds) the Video page steps
        /// through; shown to the player as their reciprocal in Hz (10 Hz .. 0.2 Hz).</summary>
        public static readonly float[] PerfIntervals = { 0.1f, 0.5f, 1f, 2f, 5f };

        /// <summary>
        /// Raised after any setting changes and was applied. Subscribers re-read the properties they
        /// care about (RopeGun's sensitivities; AudioService re-applies the mixer volumes).
        /// </summary>
        public static event Action Changed;

        /// <summary>
        /// Runtime binding overrides as InputSystem JSON, applied to the shared InputActions asset at
        /// startup. Deliberately reads/writes PlayerPrefs directly with no cached field: the applier
        /// (InputActions.LoadBindingOverrides) runs in BeforeSceneLoad whose order relative to this
        /// class's own Load is undefined — a cached field could read its default empty string when the
        /// applier queries it.
        /// </summary>
        public static string BindingOverridesJson
        {
            get => PlayerPrefs.GetString(KeyBindings, "");
            set
            {
                if (string.IsNullOrEmpty(value)) PlayerPrefs.DeleteKey(KeyBindings);
                else PlayerPrefs.SetString(KeyBindings, value);
            }
        }

        private const string KeyMaster = "Inkform.masterVolume";
        private const string KeyMusic = "Inkform.musicVolume";
        private const string KeySfx = "Inkform.sfxVolume";
        private const string KeyMuted = "Inkform.muted";
        private const string KeyMouseSens = "Inkform.mouseSensitivity";
        private const string KeyStickSens = "Inkform.stickSensitivity";
        private const string KeyDevice = "Inkform.inputDevice";
        // The .v3 suffix is a scheme version: bumping it orphans every previously saved override so a
        // changed default layout reaches players who had rebound under the old defaults (v3, 2026-10:
        // gamepad Dash/Jump/SpitBomb remapped to LB / LT / RB). Rebinding saves under the new key.
        private const string KeyBindings = "Inkform.bindingOverrides.v3";
        private const string KeyResW = "Inkform.resolutionWidth";
        private const string KeyResH = "Inkform.resolutionHeight";
        private const string KeyFullscreen = "Inkform.fullscreen";
        private const string KeyFps = "Inkform.fpsCap";
        private const string KeyVSync = "Inkform.vsync";
        private const string KeyShowFps = "Inkform.showFps";
        private const string KeyShowTimer = "Inkform.showTimer";
        private const string KeyFxIntensity = "Inkform.fxIntensity";
        private const string KeyPerfRecording = "Inkform.perfRecording";
        private const string KeyPerfInterval = "Inkform.perfInterval";
        private const string KeyRumbleLevel = "Inkform.rumbleLevel";
        private const string KeyRumbleLegacy = "Inkform.rumbleOn";     // pre-levels on/off switch, migrated on load
        private const string KeyTriggerLevel = "Inkform.triggerLevel";
        private const string KeyTriggerEffects = "Inkform.triggerEffectsOn";   // pre-levels on/off switch, migrated on load
        private const string KeyRopeWallSnap = "Inkform.ropeWallSnap";
        private const string KeyRopeBombSnap = "Inkform.ropeBombSnap";
        private const string KeyRopeDeadZone = "Inkform.ropeSnapDeadZone";
        private const string KeyRopeAdaptive = "Inkform.ropeAdaptiveSpeed";

        // ---- Lifecycle ----

        // Static fields do not clear on scene reload; with Domain Reload off, dead subscribers from
        // the previous run linger (same reason as the buses' ResetStatics)
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Changed = null;
            MasterVolume = MusicVolume = SfxVolume = 1f;
            Muted = false;
            MouseSensitivity = 1f;
            StickSensitivity = DefaultStickSensitivity;
            Device = InputDevice.KeyboardMouse;
            ResolutionWidth = ResolutionHeight = 0;
            Fullscreen = true;
            FpsCap = 120;
            VSync = false;
            ShowFps = false;
            ShowTimer = true;
            PerfRecording = false;
            PerfInterval = 1f;
            RumbleLevel = DefaultLevel;
            TriggerLevel = DefaultLevel;
            RopeWallSnap = true;
            RopeBombSnap = true;
            RopeSnapDeadZone = DefaultRopeSnapDeadZone;
            FxIntensity = 1f;
            cachedResolutions = null;
        }

        // Load + apply before the first scene: PlayerPrefs values overwrite the defaults above, and
        // graphics options must be in effect before any camera renders. Idempotent — re-runs each play
        // mode entry, which is exactly what "persisted settings win over field initializers" needs.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void LoadAndApply()
        {
            Load();
            ApplyGraphics();
            ApplyAudio();
        }

        private static void Load()
        {
            MasterVolume = PlayerPrefs.GetFloat(KeyMaster, 1f);
            MusicVolume = PlayerPrefs.GetFloat(KeyMusic, 1f);
            SfxVolume = PlayerPrefs.GetFloat(KeySfx, 1f);
            Muted = PlayerPrefs.GetInt(KeyMuted, 0) != 0;
            // Remove the preference shipped for a minimap that was never implemented.
            PlayerPrefs.DeleteKey("Inkform.mapEnabled");
            MouseSensitivity = PlayerPrefs.GetFloat(KeyMouseSens, 1f);
            StickSensitivity = PlayerPrefs.GetFloat(KeyStickSens, DefaultStickSensitivity);
            Device = (InputDevice)PlayerPrefs.GetInt(KeyDevice, (int)InputDevice.KeyboardMouse);
            Fullscreen = PlayerPrefs.GetInt(KeyFullscreen, 1) != 0;
            FpsCap = PlayerPrefs.GetInt(KeyFps, 120);
            VSync = PlayerPrefs.GetInt(KeyVSync, 0) != 0;
            ShowFps = PlayerPrefs.GetInt(KeyShowFps, 0) != 0;
            ShowTimer = PlayerPrefs.GetInt(KeyShowTimer, 1) != 0;
            PerfRecording = PlayerPrefs.GetInt(KeyPerfRecording, 0) != 0;
            PerfInterval = PlayerPrefs.GetFloat(KeyPerfInterval, 1f);
            RumbleLevel = MigrateRumbleLevel(
                PlayerPrefs.HasKey(KeyRumbleLevel), PlayerPrefs.GetInt(KeyRumbleLevel, (int)DefaultLevel),
                PlayerPrefs.HasKey(KeyRumbleLegacy), PlayerPrefs.GetInt(KeyRumbleLegacy, 1));
            if (PlayerPrefs.HasKey(KeyRumbleLegacy))
            {
                PlayerPrefs.SetInt(KeyRumbleLevel, (int)RumbleLevel);
                PlayerPrefs.DeleteKey(KeyRumbleLegacy);
            }
            TriggerLevel = MigrateRumbleLevel(
                PlayerPrefs.HasKey(KeyTriggerLevel), PlayerPrefs.GetInt(KeyTriggerLevel, (int)DefaultLevel),
                PlayerPrefs.HasKey(KeyTriggerEffects), PlayerPrefs.GetInt(KeyTriggerEffects, 1));
            if (PlayerPrefs.HasKey(KeyTriggerEffects))
            {
                PlayerPrefs.SetInt(KeyTriggerLevel, (int)TriggerLevel);
                PlayerPrefs.DeleteKey(KeyTriggerEffects);
            }
            RopeWallSnap = PlayerPrefs.GetInt(KeyRopeWallSnap, 1) != 0;
            RopeBombSnap = PlayerPrefs.GetInt(KeyRopeBombSnap, 1) != 0;
            RopeSnapDeadZone = PlayerPrefs.GetFloat(KeyRopeDeadZone, DefaultRopeSnapDeadZone);
            PlayerPrefs.DeleteKey(KeyRopeAdaptive);   // retired: adaptive speed now follows Wall Snap
            FxIntensity = PlayerPrefs.GetFloat(KeyFxIntensity, 1f);

            ResolutionWidth = PlayerPrefs.GetInt(KeyResW, 0);
            ResolutionHeight = PlayerPrefs.GetInt(KeyResH, 0);
            if (!IsResolutionSupported(ResolutionWidth, ResolutionHeight))
            {
                // Stored size is not offered by this machine (monitor changed): fall back to current
                Resolution r = Screen.currentResolution;
                ResolutionWidth = r.width;
                ResolutionHeight = r.height;
            }
        }

        // ---- Setters ----

        // Each Set: guard on change (skip the PlayerPrefs write and the Changed storm when the slider
        // value did not actually move), clamp to the legal range, persist, then notify.
        public static void SetMasterVolume(float value)
        {
            value = Mathf.Clamp01(value);
            if (Mathf.Approximately(MasterVolume, value)) return;
            MasterVolume = value;
            PlayerPrefs.SetFloat(KeyMaster, value);
            Changed?.Invoke();
        }

        /// <summary>Music track volume. Applied by MusicPlayer, which recomputes running music
        /// volume every frame — slider drags land on the next frame, no restart needed.</summary>
        public static void SetMusicVolume(float value)
        {
            value = Mathf.Clamp01(value);
            if (Mathf.Approximately(MusicVolume, value)) return;
            MusicVolume = value;
            PlayerPrefs.SetFloat(KeyMusic, value);
            Changed?.Invoke();
        }

        public static void SetSfxVolume(float value)
        {
            value = Mathf.Clamp01(value);
            if (Mathf.Approximately(SfxVolume, value)) return;
            SfxVolume = value;
            PlayerPrefs.SetFloat(KeySfx, value);
            Changed?.Invoke();
        }

        public static void SetMuted(bool value)
        {
            if (Muted == value) return;
            Muted = value;
            PlayerPrefs.SetInt(KeyMuted, value ? 1 : 0);
            ApplyAudio();
            Changed?.Invoke();
        }

        public static void SetMouseSensitivity(float value)
        {
            value = Mathf.Clamp(value, MinSensitivity, MaxSensitivity);
            if (Mathf.Approximately(MouseSensitivity, value)) return;
            MouseSensitivity = value;
            PlayerPrefs.SetFloat(KeyMouseSens, value);
            Changed?.Invoke();
        }

        public static void SetStickSensitivity(float value)
        {
            value = Mathf.Clamp(value, MinSensitivity, MaxSensitivity);
            if (Mathf.Approximately(StickSensitivity, value)) return;
            StickSensitivity = value;
            PlayerPrefs.SetFloat(KeyStickSens, value);
            Changed?.Invoke();
        }

        public static void SetDevice(InputDevice value)
        {
            if (Device == value) return;
            Device = value;
            PlayerPrefs.SetInt(KeyDevice, (int)value);
            Changed?.Invoke();
        }

        public static void SetResolution(int width, int height)
        {
            if (!IsResolutionSupported(width, height)) return;
            if (ResolutionWidth == width && ResolutionHeight == height) return;
            ResolutionWidth = width;
            ResolutionHeight = height;
            PlayerPrefs.SetInt(KeyResW, width);
            PlayerPrefs.SetInt(KeyResH, height);
            ApplyGraphics();
            Changed?.Invoke();
        }

        public static void SetFullscreen(bool value)
        {
            if (Fullscreen == value) return;
            Fullscreen = value;
            PlayerPrefs.SetInt(KeyFullscreen, value ? 1 : 0);
            ApplyGraphics();
            Changed?.Invoke();
        }

        /// <summary>Frame cap in fps; 0 = uncapped. Ignored while VSync is on (Unity's own rule).</summary>
        public static void SetFpsCap(int value)
        {
            if (value < 0) value = 0;
            if (FpsCap == value) return;
            FpsCap = value;
            PlayerPrefs.SetInt(KeyFps, value);
            ApplyGraphics();
            Changed?.Invoke();
        }

        public static void SetVSync(bool value)
        {
            if (VSync == value) return;
            VSync = value;
            PlayerPrefs.SetInt(KeyVSync, value ? 1 : 0);
            ApplyGraphics();
            Changed?.Invoke();
        }

        public static void SetShowFps(bool value)
        {
            if (ShowFps == value) return;
            ShowFps = value;
            PlayerPrefs.SetInt(KeyShowFps, value ? 1 : 0);
            Changed?.Invoke();
        }

        public static void SetShowTimer(bool value)
        {
            if (ShowTimer == value) return;
            ShowTimer = value;
            PlayerPrefs.SetInt(KeyShowTimer, value ? 1 : 0);
            Changed?.Invoke();
        }

        public static void SetFxIntensity(float value)
        {
            value = Mathf.Clamp01(value);
            if (Mathf.Approximately(FxIntensity, value)) return;
            FxIntensity = value;
            PlayerPrefs.SetFloat(KeyFxIntensity, value);
            Changed?.Invoke();
        }

        public static void SetPerfRecording(bool value)
        {
            if (PerfRecording == value) return;
            PerfRecording = value;
            PlayerPrefs.SetInt(KeyPerfRecording, value ? 1 : 0);
            Changed?.Invoke();
        }

        /// <summary>Performance recorder sampling interval in seconds. Only the PerfIntervals
        /// values are accepted — the Video page stepper cycles exactly those, and an off-list value
        /// would desync its display.</summary>
        public static void SetPerfInterval(float value)
        {
            if (System.Array.IndexOf(PerfIntervals, value) < 0) return;
            if (Mathf.Approximately(PerfInterval, value)) return;
            PerfInterval = value;
            PlayerPrefs.SetFloat(KeyPerfInterval, value);
            Changed?.Invoke();
        }

        public static void SetRumbleLevel(RumbleLevel value)
        {
            if (RumbleLevel == value) return;
            RumbleLevel = value;
            PlayerPrefs.SetInt(KeyRumbleLevel, (int)value);
            Changed?.Invoke();
        }

        /// <summary>A stored level (rumble, and the trigger level the same way): the levels key
        /// when present, else the old on/off switch (off stays Off, on becomes High — the strength
        /// the switch used to give), else the default. Out-of-range stored values clamp. Pure so
        /// tests can pin the migration.</summary>
        public static RumbleLevel MigrateRumbleLevel(bool hasLevel, int level, bool hasLegacy, int legacyOn)
        {
            if (hasLevel)
                return (RumbleLevel)System.Math.Max((int)RumbleLevel.Off, System.Math.Min((int)RumbleLevel.High, level));
            if (hasLegacy) return legacyOn != 0 ? RumbleLevel.High : RumbleLevel.Off;
            return DefaultLevel;
        }

        public static void SetTriggerLevel(RumbleLevel value)
        {
            if (TriggerLevel == value) return;
            TriggerLevel = value;
            PlayerPrefs.SetInt(KeyTriggerLevel, (int)value);
            Changed?.Invoke();
        }

        // ---- Grapping hook setters (RopeGun reads the properties live) ----

        public static void SetRopeWallSnap(bool value)
        {
            if (RopeWallSnap == value) return;
            RopeWallSnap = value;
            PlayerPrefs.SetInt(KeyRopeWallSnap, value ? 1 : 0);
            Changed?.Invoke();
        }

        public static void SetRopeBombSnap(bool value)
        {
            if (RopeBombSnap == value) return;
            RopeBombSnap = value;
            PlayerPrefs.SetInt(KeyRopeBombSnap, value ? 1 : 0);
            Changed?.Invoke();
        }

        public static void SetRopeSnapDeadZone(float value)
        {
            value = Mathf.Clamp01(value);
            if (Mathf.Approximately(RopeSnapDeadZone, value)) return;
            RopeSnapDeadZone = value;
            PlayerPrefs.SetFloat(KeyRopeDeadZone, value);
            Changed?.Invoke();
        }

        public static void ResetToDefaults()
        {
            MasterVolume = MusicVolume = SfxVolume = 1f;
            Muted = false;
            MouseSensitivity = 1f;
            StickSensitivity = DefaultStickSensitivity;
            Device = InputDevice.KeyboardMouse;
            Fullscreen = true;
            FpsCap = 120;
            VSync = false;
            ShowFps = false;
            ShowTimer = true;
            PerfRecording = false;
            PerfInterval = 1f;
            RumbleLevel = DefaultLevel;
            TriggerLevel = DefaultLevel;
            RopeWallSnap = true;
            RopeBombSnap = true;
            RopeSnapDeadZone = DefaultRopeSnapDeadZone;
            FxIntensity = 1f;
            Resolution r = Screen.currentResolution;
            ResolutionWidth = r.width;
            ResolutionHeight = r.height;

            // Per-key delete rather than PlayerPrefs.DeleteAll: other systems may own keys one day,
            // and a settings reset must not blow them away
            PlayerPrefs.DeleteKey(KeyMaster);
            PlayerPrefs.DeleteKey(KeyMusic);
            PlayerPrefs.DeleteKey(KeySfx);
            PlayerPrefs.DeleteKey(KeyMuted);
            PlayerPrefs.DeleteKey("Inkform.mapEnabled");
            PlayerPrefs.DeleteKey(KeyMouseSens);
            PlayerPrefs.DeleteKey(KeyStickSens);
            PlayerPrefs.DeleteKey(KeyDevice);
            PlayerPrefs.DeleteKey(KeyBindings);
            PlayerPrefs.DeleteKey(KeyResW);
            PlayerPrefs.DeleteKey(KeyResH);
            PlayerPrefs.DeleteKey(KeyFullscreen);
            PlayerPrefs.DeleteKey(KeyFps);
            PlayerPrefs.DeleteKey(KeyVSync);
            PlayerPrefs.DeleteKey(KeyShowFps);
            PlayerPrefs.DeleteKey(KeyShowTimer);
            PlayerPrefs.DeleteKey(KeyFxIntensity);
            PlayerPrefs.DeleteKey(KeyPerfRecording);
            PlayerPrefs.DeleteKey(KeyPerfInterval);
            PlayerPrefs.DeleteKey(KeyRumbleLevel);
            PlayerPrefs.DeleteKey(KeyRumbleLegacy);
            PlayerPrefs.DeleteKey(KeyTriggerLevel);
            PlayerPrefs.DeleteKey(KeyTriggerEffects);
            PlayerPrefs.DeleteKey(KeyRopeWallSnap);
            PlayerPrefs.DeleteKey(KeyRopeBombSnap);
            PlayerPrefs.DeleteKey(KeyRopeDeadZone);
            PlayerPrefs.DeleteKey(KeyRopeAdaptive);

            ApplyGraphics();
            ApplyAudio();
            Changed?.Invoke();
        }

        // ---- Audio application ----

        /// <summary>
        /// Lands Muted on the audio engine. Goes through the global AudioListener.volume rather than
        /// the per-voice volume maths: one global switch silences every voice and the music on the
        /// same frame, and unmuting restores the exact mix without recomputing anything. Nothing
        /// else in the project touches this global.
        /// </summary>
        private static void ApplyAudio()
        {
            AudioListener.volume = Muted ? 0f : 1f;
        }

        // ---- Graphics application ----

        private static void ApplyGraphics()
        {
            if (ResolutionWidth > 0 && ResolutionHeight > 0)
            {
                // FullScreenWindow (borderless) rather than ExclusiveFullScreen: switching out of an
                // exclusive mode mid-editor play can hang the editor; borderless applies everywhere.
                Screen.SetResolution(ResolutionWidth, ResolutionHeight,
                    Fullscreen ? FullScreenMode.FullScreenWindow : FullScreenMode.Windowed);
            }

            // VSync wins over the frame cap: targetFrameRate must be -1 for vsync to take effect,
            // mirroring that keeps the two options from silently fighting
            QualitySettings.vSyncCount = VSync ? 1 : 0;
            Application.targetFrameRate = VSync ? -1 : FpsCap;
        }

        // ---- Resolution list for the Video page ----

        private static List<Resolution> cachedResolutions;

        /// <summary>Resolutions this machine offers, deduplicated (Unity repeats them per refresh
        /// rate). Empty screen list falls back to the current resolution, so the UI always has ≥1.</summary>
        public static IReadOnlyList<Resolution> AvailableResolutions
        {
            get
            {
                if (cachedResolutions == null)
                {
                    cachedResolutions = new List<Resolution>();
                    var seen = new HashSet<(int width, int height)>();
                    foreach (Resolution r in Screen.resolutions)
                    {
                        if (seen.Add((r.width, r.height)))
                            cachedResolutions.Add(r);
                    }
                    if (cachedResolutions.Count == 0)
                        cachedResolutions.Add(Screen.currentResolution);
                }
                return cachedResolutions;
            }
        }

        private static bool IsResolutionSupported(int width, int height)
        {
            foreach (Resolution r in AvailableResolutions)
            {
                if (r.width == width && r.height == height) return true;
            }
            return false;
        }
    }
}

using System.Collections.Generic;
using Inkform.Ability;
using Inkform.Bus;
using Inkform.Input;
using Inkform.Item;
using Inkform.Level;
using Inkform.Life;
using Inkform.Player;
using Inkform.Settings;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.DualShock;

namespace Inkform.Fx.Haptics
{
    /// <summary>
    /// Gamepad haptics director: turns "what happened in the game" into motor rumble and trigger
    /// feel, on the one pad the player is holding (ActivePadTracker). Same director pattern as
    /// FxDirector / AudioDirector — all tuning lives in this Inspector; RopeGun / PlayerHandler
    /// never know haptics exist.
    ///
    /// Only the DualSense is driven here. Every other pad (Xbox, DualShock 4, Switch, generic) is
    /// GenericPadHaptics' — one whole-pad shake on contacts and the rope gun.
    ///
    /// Two layers, rendered by DualSenseHaptics (through IPadHaptics):
    ///  - Motors: every event adds clips to a HapticMixer (delay + per-motor envelope); the mix is
    ///    the per-motor max, mapped through a MotorCurve for the player's rumble level (Off / Low /
    ///    Medium / High). Left/low = heavy grip motor, right/high = light motor. Touching a surface
    ///    rumbles from that side: ground = a heavy thud in both grips, ceiling = from the top (the
    ///    triggers; a quick double tap with trigger effects off), walls = only the motor on that
    ///    side.
    ///  - Triggers: the rope gun's and the jump trigger hold the same standing Bow while playing
    ///    (resistance, a break, a snap-back), plus short pulses (rope fired and take-off share
    ///    one kick; hitting a ceiling), all scaled by the player's Trigger Effects level — a level
    ///    of its own, so the triggers work with rumble off and the other way round. The sides follow the gamepad bindings, so a rebind moves
    ///    the feel with the action; a face-button binding gets none.
    ///
    /// Timing is unscaled. While the game is frozen (pause / hitstop) the gameplay motors read
    /// zero and every clip keeps its remaining time; the menus' own feedback (clicks, toggles, a
    /// value bumping its end) has a mixer of its own that keeps running, so it is felt in the
    /// pause menu too. Menus and pause release the triggers. Lives on the
    /// GameManager (the persistent host).
    /// </summary>
    public class HapticsDirector : MonoBehaviour
    {
        // All motor strengths are tuned at half motor power (the player-facing "50% of full"
        // feel); the tier references are Celeste's rumble tables (Light 0.3 / Medium 0.8 / Strong
        // 2.0 at full power, Short 0.1 / Medium 0.25 length).

        [Header("Rumble levels")]
        [Tooltip("DualSense motor curve at Low / Medium / High. High = the tuned strengths as they are")]
        [SerializeField] private MotorCurve[] dualSenseLevels =
        {
            new MotorCurve(0.35f, 0f), new MotorCurve(0.65f, 0f), new MotorCurve(1f, 0f),
        };
        [Tooltip("Adaptive-trigger strength at the Trigger Effects level Low / Medium / High: scales resistance, snap-back and buzz amplitude. High = the tuned strengths as they are")]
        [SerializeField] private float[] triggerLevels = { 0.35f, 0.65f, 1f };

        [Header("Contact (ground / ceiling / walls)")]
        [Tooltip("Touches slower than this (into the surface) do not rumble: sliding along a wall, a small step")]
        [SerializeField] private float minImpactSpeed = 1.5f;
        [Tooltip("At or above this impact speed a contact rumbles at Contact Max")]
        [SerializeField] private float maxImpactSpeed = 14f;
        [SerializeField] private float contactMin = 0.15f;
        [SerializeField] private float contactMax = 0.4f;
        [Tooltip("Ground: a heavy thud in both grips, low motor leading, fading out")]
        [SerializeField] private float groundDuration = 0.12f;
        [SerializeField, Range(0f, 1f)] private float groundHighShare = 0.35f;
        [Tooltip("Ceiling: both triggers (the top of the pad) buzz, plus a faint high tick")]
        [SerializeField] private float ceilingTriggerDuration = 0.06f;
        [SerializeField, Range(1, 255)] private int ceilingTriggerFrequency = 160;
        [SerializeField, Range(0f, 1f)] private float ceilingHighShare = 0.4f;
        [Tooltip("Ceiling with trigger effects off: a quick double tap, high motor leading")]
        [SerializeField] private float ceilingTapDuration = 0.035f;
        [SerializeField] private float ceilingTapGap = 0.045f;
        [Tooltip("Walls: only the motor on that side, sharp and short")]
        [SerializeField] private float wallDuration = 0.08f;
        [Tooltip("The right motor is the small high-frequency one: lift it so both walls feel equally hard")]
        [SerializeField] private float rightSideBoost = 1.4f;

        [Header("Triggers: rope gun and jump (the same Bow)")]
        [Tooltip("Resistance begins at this zone (0 released .. 9 fully pulled)")]
        [SerializeField, Range(0, 8)] private int triggerStart = 2;
        [Tooltip("The resistance breaks here; past it the trigger is free, and lets go with a snap-back")]
        [SerializeField, Range(1, 8)] private int triggerBreak = 5;
        [Tooltip("The resistance before the break, at High (Medium / Low scale it: 3 → 2 / 1)")]
        [SerializeField, Range(1, 8)] private int triggerStrength = 3;
        [SerializeField, Range(1, 8)] private int triggerSnapForce = 5;
        [Tooltip("Trigger buzz when the rope fires / on take-off")]
        [SerializeField] private float kickDuration = 0.06f;
        [SerializeField, Range(1, 8)] private int kickAmplitude = 6;
        [SerializeField, Range(1, 255)] private int kickFrequency = 90;

        [Header("Blast (shockwave)")]
        [SerializeField] private float blastStrength = 0.5f;
        [SerializeField] private float blastDuration = 0.22f;
        [SerializeField] private float blastFalloff = 20f;                 // blasts farther than this are completely unfelt
        [Tooltip("Shockwave speed: drives the left/right motor delay. Lower = stronger sweep feel")]
        [SerializeField] private float shockSpeed = 30f;
        [Tooltip("Player body half-width, used to compute the shockwave's arrival-time difference across the body")]
        [SerializeField] private float playerHalfWidth = 0.5f;

        [Header("Rope")]
        [SerializeField] private float fireStrength = 0.1f;
        [SerializeField] private float fireDuration = 0.08f;
        [SerializeField] private float hitStrength = 0.28f;
        [SerializeField] private float hitDuration = 0.15f;

        [Header("Life")]
        [SerializeField] private float deathStrength = 0.2f;       // Medium
        [SerializeField] private float deathDuration = 0.25f;      // Medium
        [SerializeField] private float checkpointStrength = 0.08f; // Light
        [SerializeField] private float checkpointDuration = 0.25f;
        [SerializeField] private float respawnStrength = 0.08f;    // Light — a teleport home, not an impact
        [SerializeField] private float respawnDuration = 0.1f;

        [Header("Dash")]
        [SerializeField] private float dashStrength = 0.2f;
        [SerializeField] private float dashDuration = 0.1f;
        [SerializeField] private float dashFailStrength = 0.08f;   // no fuel: a faint tick, not a reward
        [SerializeField] private float dashFailDuration = 0.06f;

        [Header("Hazard")]
        [SerializeField] private float tickStrength = 0.08f;       // bomb fuse warning pulse
        [SerializeField] private float tickDuration = 0.05f;
        [SerializeField] private float brokenStrength = 0.08f;     // wall shattered nearby
        [SerializeField] private float brokenDuration = 0.1f;

        [Header("Items")]
        [SerializeField] private float storeStrength = 0.2f;       // swallow
        [SerializeField] private float storeDuration = 0.1f;
        [SerializeField] private float releaseStrength = 0.08f;    // spit (Celeste's Drop tier)
        [SerializeField] private float releaseDuration = 0.1f;
        [SerializeField] private float upgradeStrength = 0.2f;     // capacity / ability — permanent progress
        [SerializeField] private float upgradeDuration = 0.25f;
        [Tooltip("Touching a capacity crystal; the full upgrade rumble waits for it to land in the HUD")]
        [SerializeField] private float capacityPickupStrength = 0.1f;
        [SerializeField] private float capacityPickupDuration = 0.08f;

        [Header("UI (plays in menus and through a pause)")]
        [SerializeField] private float uiStrength = 0.08f;
        [SerializeField] private float uiClickDuration = 0.1f;
        [SerializeField] private float uiToggleDuration = 0.1f;
        [Tooltip("A value pushed past its end (option row / bar in the settings)")]
        [SerializeField] private float uiBumpStrength = 0.3f;
        [SerializeField] private float uiBumpDuration = 0.08f;

        [Header("Debug (read-only, refreshed in Play mode)")]
        [SerializeField] private string debugPad;
        [SerializeField] private string debugRoute;
        [SerializeField] private float debugLow;
        [SerializeField] private float debugHigh;
        [SerializeField] private bool debugWriteOk;
        [SerializeField] private bool debugFrozen;
        [SerializeField] private bool debugFocused;

        private enum Side { None, Left, Right }

        private struct TriggerPulse
        {
            public TriggerEffect effect;
            public float remaining;
            public float delay;
        }

        private const float BindingRefreshSeconds = 0.25f;


        private readonly HapticMixer mixer = new HapticMixer();
        private readonly HapticMixer uiMixer = new HapticMixer();   // menu feedback: never frozen
        private readonly List<IPadHaptics> retiring = new List<IPadHaptics>();
        private IPadHaptics output;
        private TriggerPulse leftPulse, rightPulse;
        private Side ropeSide = Side.Right, jumpSide = Side.Left;
        private float bindingRefreshIn;
        private bool hasFocus = true;
        private string loggedRoute;

        void OnEnable()
        {
            PlayerBus.StateChanged += OnPlayerState;
            PlayerBus.Contacted += OnContact;
            PlayerBus.DashAttempted += OnDashAttempted;
            HazardBus.Blast += OnBlast;
            HazardBus.Ticked += OnTicked;
            HazardBus.Broken += OnBroken;
            RopeGunBus.Fired += OnRopeFired;
            RopeGunBus.Hit += OnRopeHit;
            LifeBus.Died += OnDied;
            LifeBus.CheckpointSet += OnCheckpointSet;
            LifeBus.Respawned += OnRespawned;
            ItemBus.ItemStored += OnItemStored;
            ItemBus.ItemReleased += OnItemReleased;
            ItemBus.InventoryCapacityUpgraded += OnCapacityUpgraded;
            ItemBus.CapacityFlight += OnCapacityFlight;
            UiBus.Clicked += OnUiClicked;
            UiBus.Toggled += OnUiToggled;
            UiBus.Bumped += OnUiBumped;
        }

        void OnDisable()
        {
            PlayerBus.StateChanged -= OnPlayerState;
            PlayerBus.Contacted -= OnContact;
            PlayerBus.DashAttempted -= OnDashAttempted;
            HazardBus.Blast -= OnBlast;
            HazardBus.Ticked -= OnTicked;
            HazardBus.Broken -= OnBroken;
            RopeGunBus.Fired -= OnRopeFired;
            RopeGunBus.Hit -= OnRopeHit;
            LifeBus.Died -= OnDied;
            LifeBus.CheckpointSet -= OnCheckpointSet;
            LifeBus.Respawned -= OnRespawned;
            ItemBus.ItemStored -= OnItemStored;
            ItemBus.ItemReleased -= OnItemReleased;
            ItemBus.InventoryCapacityUpgraded -= OnCapacityUpgraded;
            ItemBus.CapacityFlight -= OnCapacityFlight;
            UiBus.Clicked -= OnUiClicked;
            UiBus.Toggled -= OnUiToggled;
            UiBus.Bumped -= OnUiBumped;

            SilenceAll();
        }

        // An adaptive-trigger effect is held by the pad itself: it must be released before the
        // game lets go of the device, or the trigger stays stiff after quitting
        void OnApplicationQuit() => SilenceAll();

        void OnApplicationFocus(bool focus)
        {
            hasFocus = focus;
            if (!focus) output?.Silence();
        }

        private void SilenceAll()
        {
            mixer.Clear();
            uiMixer.Clear();
            leftPulse = rightPulse = default;
            output?.Silence();
            output = null;
            foreach (IPadHaptics pad in retiring) pad.Silence();
            retiring.Clear();
        }

        // ---- Per frame ----

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            RetireSilenced();
            SelectPad();

            bool frozen = GameTimeController.Instance != null && GameTimeController.Instance.IsFrozen;
            float step = frozen ? 0f : dt;

            mixer.Tick(step, out float low, out float high);
            uiMixer.Tick(dt, out float uiLow, out float uiHigh);   // menu feedback runs through a pause
            TickPulse(ref leftPulse, step);
            TickPulse(ref rightPulse, step);

            debugFrozen = frozen;
            debugFocused = hasFocus;
            if (output == null || !hasFocus)
            {
                debugPad = output == null ? "(none)" : debugPad;
                debugRoute = output == null ? "(no output)" : debugRoute;
                return;
            }

            RefreshTriggerSides(dt);
            // The trigger feel at the player's Trigger Effects level (Off never gets here: TriggerFor)
            float triggerScale = TriggerScaleFor(SettingsStore.TriggerLevel);
            TriggerEffect left = TriggerFor(Side.Left, leftPulse, frozen).Scaled(triggerScale);
            TriggerEffect right = TriggerFor(Side.Right, rightPulse, frozen).Scaled(triggerScale);

            // Pause / hitstop: gameplay motors read zero while every clip keeps its remaining
            // time; the menus' own feedback plays on top either way
            if (frozen) low = high = 0f;
            low = Mathf.Max(low, uiLow);
            high = Mathf.Max(high, uiHigh);
            // Rumble off with trigger effects on: the triggers alone
            if (!SettingsStore.Rumble) low = high = 0f;
            output.Curve = CurveFor(SettingsStore.RumbleLevel);
            output.Apply(low, high, left, right, dt);

            debugPad = output.Pad != null ? $"{output.Pad.displayName} ({output.Pad.layout})" : "(gone)";
            debugRoute = output.Route;
            debugLow = output.SentLow;
            debugHigh = output.SentHigh;
            debugWriteOk = output.LastWriteOk;
            if (debugRoute != loggedRoute)
            {
                loggedRoute = debugRoute;
                Debug.Log($"[Haptics] output → {debugPad} via {debugRoute}", this);
            }
        }

        // The pad the player holds now; the previous one is silenced (motors and triggers) before
        // the switch, so only one pad ever feels anything
        // (ActivePadTracker already holds the pad through a short keyboard grace)
        private void SelectPad()
        {
            // Motors and triggers have a level each: the pad is driven while either is on
            Gamepad target = SettingsStore.Rumble || SettingsStore.TriggerEffects ? ActivePadTracker.Active : null;

            // Only a DualSense: every other pad belongs to GenericPadHaptics
            if (!(target is DualSenseGamepadHID)) target = null;

            if (output != null && target != null && output.Covers(target)) return;
            Retarget(target);
        }

        private void Retarget(Gamepad target)
        {
            if (output == null && target == null) return;
            if (output != null && !output.Silence()) retiring.Add(output);
            output = PadHapticsFactory.For(target);
        }

        // A DualSense busy with a previous command refuses the silencing write: retry each frame
        private void RetireSilenced()
        {
            for (int i = retiring.Count - 1; i >= 0; i--)
                if (retiring[i].Silence()) retiring.RemoveAt(i);
        }

        // The player's rumble level on the DualSense's motors (Off: Update zeroes the motors)
        private MotorCurve CurveFor(RumbleLevel level)
        {
            int index = (int)level - 1;
            if (dualSenseLevels == null || index < 0 || index >= dualSenseLevels.Length) return MotorCurve.Identity;
            return dualSenseLevels[index];
        }

        // The player's Trigger Effects level as a share of the tuned trigger strengths
        private float TriggerScaleFor(RumbleLevel level)
        {
            int index = (int)level - 1;
            if (triggerLevels == null || index < 0 || index >= triggerLevels.Length) return 1f;
            return Mathf.Clamp01(triggerLevels[index]);
        }

        // ---- Triggers ----

        private static void TickPulse(ref TriggerPulse pulse, float step)
        {
            if (pulse.remaining <= 0f) return;
            if (pulse.delay > 0f)
            {
                pulse.delay -= step;
                return;
            }
            pulse.remaining -= step;
        }

        private TriggerEffect TriggerFor(Side side, in TriggerPulse pulse, bool frozen)
        {
            if (!SettingsStore.TriggerEffects) return TriggerEffect.Off;

            // A pulse plays over the standing effect (never during a freeze — a buzzing trigger
            // in a hitstop or the pause menu reads as a fault)
            if (!frozen && pulse.remaining > 0f && pulse.delay <= 0f) return pulse.effect;

            if (!InGameplay) return TriggerEffect.Off;
            if ((side == ropeSide && AbilityStore.Owns(AbilityIds.RopeGun)) || side == jumpSide)
                return TriggerEffect.Bow(triggerStart, Mathf.Max(triggerBreak, triggerStart + 1), triggerStrength, triggerSnapForce);
            return TriggerEffect.Off;
        }

        private TriggerEffect Kick => TriggerEffect.Vibration(0, kickAmplitude, kickFrequency);

        // Standing trigger feel only while actually playing: menus, pause, transitions and death
        // all release the triggers
        private static bool InGameplay =>
            GameStateStore.Current == GameStateStore.GameState.Playing
            && !LifeBus.IsDead
            && PlayerBus.Player != null
            && !(SceneDirector.Instance != null && SceneDirector.Instance.IsTransitioning);

        private void PulseTrigger(Side side, TriggerEffect effect, float duration, float delay = 0f)
        {
            var pulse = new TriggerPulse { effect = effect, remaining = duration, delay = delay };
            if (side == Side.Left) leftPulse = pulse;
            else if (side == Side.Right) rightPulse = pulse;
        }

        // Which trigger hosts Jump / RopeFire follows the gamepad bindings (rebinds included);
        // polled a few times a second because a rebind raises no event of its own
        private void RefreshTriggerSides(float dt)
        {
            bindingRefreshIn -= dt;
            if (bindingRefreshIn > 0f) return;
            bindingRefreshIn = BindingRefreshSeconds;

            var player = InputActions.Wrapper.Player;
            ropeSide = SideOf(player.RopeFire);
            jumpSide = SideOf(player.Jump);
            if (jumpSide == ropeSide) jumpSide = Side.None;   // one trigger, one feel: the rope's wins
        }

        private static Side SideOf(InputAction action)
        {
            int index = BindingTools.FindBindingIndex(action, BindingTools.GamepadGroup, null);
            if (index < 0) return Side.None;
            string path = action.bindings[index].effectivePath;
            if (string.IsNullOrEmpty(path)) return Side.None;
            if (path.EndsWith("leftTrigger")) return Side.Left;
            if (path.EndsWith("rightTrigger")) return Side.Right;
            return Side.None;
        }

        // ---- Events ----

        private void OnPlayerState(PlayerState state)
        {
            if (state == PlayerState.JumpUp && jumpSide != Side.None)
                PulseTrigger(jumpSide, Kick, kickDuration);
        }

        // Contact: the rumble comes from the side that was hit, scaled by how hard the player ran
        // into it. Ground thuds in both grips (low leading); the ceiling comes from the top — the
        // triggers, or a quick high double tap with trigger effects off; a wall shakes only its
        // own side.
        private void OnContact(ContactSide side, float impactSpeed)
        {
            if (impactSpeed < minImpactSpeed) return;
            float s = Mathf.Lerp(contactMin, contactMax, Mathf.InverseLerp(minImpactSpeed, maxImpactSpeed, impactSpeed));

            switch (side)
            {
                case ContactSide.Down:
                    mixer.Add(s, 0f, s * groundHighShare, 0f, groundDuration);
                    break;

                case ContactSide.Up:
                    // Trigger feel switched off: fall back to the double tap
                    if (output != null && output.NativeTriggers && SettingsStore.TriggerEffects)
                    {
                        int amplitude = Mathf.Clamp(Mathf.RoundToInt(s / contactMax * 8f), 1, 8);
                        var buzz = TriggerEffect.Vibration(0, amplitude, ceilingTriggerFrequency);
                        PulseTrigger(Side.Left, buzz, ceilingTriggerDuration);
                        PulseTrigger(Side.Right, buzz, ceilingTriggerDuration);
                        mixer.Add(0f, s * ceilingHighShare, ceilingTapDuration);
                    }
                    else
                    {
                        mixer.Add(s * 0.5f, s, ceilingTapDuration);
                        mixer.Add(s * 0.5f, s, ceilingTapDuration, ceilingTapDuration + ceilingTapGap);
                    }
                    break;

                case ContactSide.Left:
                    mixer.Add(s, 0f, 0f, 0f, wallDuration);
                    break;

                case ContactSide.Right:
                    float r = Mathf.Min(1f, s * rightSideBoost);
                    mixer.Add(0f, 0f, r, 0f, wallDuration);
                    break;
            }
        }

        // Shockwave: the blast front reaches the near half of the body first, so that motor fires
        // first; the far half fires after the travel-time difference. Overall strength attenuates by
        // distance from the blast center.
        private void OnBlast(Vector2 center, float radius, float force)
        {
            if (PlayerBus.Player == null) return;
            Vector3 p = PlayerBus.Player.transform.position;

            float dist = Vector2.Distance(center, p);
            float k = blastFalloff <= 0f ? 1f : Mathf.Clamp01(1f - dist / blastFalloff);
            if (k <= 0f) return;

            float strength = blastStrength * k;
            float w = Mathf.Max(0f, playerHalfWidth);

            // Distances to the left/right half of the body; their difference is the sweep delay
            float dL = Vector2.Distance(center, p + new Vector3(-w, 0f, 0f));
            float dR = Vector2.Distance(center, p + new Vector3(w, 0f, 0f));

            float speed = Mathf.Max(0.01f, shockSpeed);
            mixer.Add(strength, 0f, blastDuration, dL / speed);   // left half
            mixer.Add(0f, strength, blastDuration, dR / speed);   // right half
        }

        private void OnRopeFired(Vector2 dir)
        {
            AddDirectional(dir, fireStrength, fireDuration);
            if (ropeSide != Side.None)
                PulseTrigger(ropeSide, Kick, kickDuration);
        }

        private void OnRopeHit(Vector2 dir) => AddDirectional(dir, hitStrength, hitDuration);

        // Fires into the death hitstop (0.1-0.18s depending on cause): the frozen mixer holds the
        // rumble back until the freeze lifts, so it lands inside the death pause — the Celeste beat
        private void OnDied(DeathContext ctx) => mixer.Add(deathStrength, deathStrength, deathDuration);

        private void OnCheckpointSet(Vector2 pos) => mixer.Add(checkpointStrength, checkpointStrength, checkpointDuration);

        private void OnRespawned(GameObject victim, Vector2 pos) => mixer.Add(respawnStrength, respawnStrength, respawnDuration);

        private void OnDashAttempted(Vector2 pos, bool succeeded)
        {
            if (succeeded) mixer.Add(dashStrength, dashStrength, dashDuration);
            else mixer.Add(dashFailStrength, dashFailStrength, dashFailDuration);
        }

        private void OnTicked(Vector2 pos, int step, int total) => mixer.Add(tickStrength, tickStrength, tickDuration);

        private void OnBroken(Vector2 pos) => mixer.Add(brokenStrength, brokenStrength, brokenDuration);

        private void OnItemStored(InventoryItemDefinition item) => mixer.Add(storeStrength, storeStrength, storeDuration);

        private void OnItemReleased(InventoryItemDefinition item, Vector2 pos, Vector2 velocity) =>
            mixer.Add(releaseStrength, releaseStrength, releaseDuration);

        // The capacity crystal freezes the world for its flight into the HUD, and the gameplay mixer
        // holds still while frozen — so both of its beats go through the never-frozen UI mixer: a
        // light tick on the touch, the upgrade rumble when the crystal lands
        private void OnCapacityUpgraded(Vector2 pos, int increase) =>
            uiMixer.Add(capacityPickupStrength, capacityPickupStrength, capacityPickupDuration);

        private void OnCapacityFlight(CapacityFlightPhase phase, int increase)
        {
            if (phase == CapacityFlightPhase.Land) uiMixer.Add(upgradeStrength, upgradeStrength, upgradeDuration);
        }

        private void OnUiClicked() => uiMixer.Add(uiStrength, uiStrength, uiClickDuration);

        private void OnUiToggled(bool on) => uiMixer.Add(uiStrength, uiStrength, uiToggleDuration);

        private void OnUiBumped() => uiMixer.Add(uiBumpStrength, uiBumpStrength, uiBumpDuration);

        // Directional events split the strength by the horizontal component of the direction
        private void AddDirectional(Vector2 dir, float strength, float duration)
        {
            float x = dir.sqrMagnitude > 0.0001f ? dir.normalized.x : 0f;
            mixer.Add(strength * (0.5f - 0.5f * x), strength * (0.5f + 0.5f * x), duration);
        }
    }
}

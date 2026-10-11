using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using Inkform.Bus;
using Inkform.Fx.Haptics;
using Inkform.Player;
using Inkform.Settings;

namespace Inkform.Input
{
    /// <summary>
    /// Input layer: forwards the actions from the shared InputActions asset to PlayerHandler.
    ///
    /// Key bindings have a single source — the InputSystem_Actions.inputactions asset (rebind there;
    /// Unity regenerates InputSystem_Actions.cs). User remaps live as the official runtime binding
    /// override layer on top of it, loaded into the shared asset at startup and edited in the
    /// Controls tab — this class never patches bindings, it only reads the resulting actions.
    ///
    /// Device filtering lives here as the single choke point: the Controls tab picks one input family
    /// (KeyboardMouse or Gamepad), and every forwarded action on the other family is ignored.
    /// </summary>
    [DefaultExecutionOrder(-8000)]
    public class InputHandler : MonoBehaviour
    {
        // Shared wrapper (InputActions) — not `new`ed here anymore, see InputActions.
        private InputSystem_Actions playerInput;

        // The seven actions actually used in this game from the Player map, all taken straight from
        // the generated wrapper
        private InputAction move;
        private InputAction aim;
        private InputAction jump;
        private InputAction dash;
        private InputAction ropeFire;
        private InputAction spitBomb;
        private InputAction interact;
        private bool actionsInitialized;
        private readonly HashSet<Object> gameplayInputLockOwners = new HashSet<Object>();

        // Held-button actions feeding the device auto-detection (a held button = deliberate input)
        private InputAction[] pressButtons;

        // The current scene's player, rebound from PlayerBus (never serialized: the persistent
        // GameManager outlives every scene's player)
        private PlayerHandler player;

        // A keyboard is discrete 0/±1; here we synthesize analog stick strength from press duration:
        // the longer a key is held the closer to full strength (rampUpTime), recentering on release
        // (rampDownTime), then run through a strength curve (gentle start, crisp end — like pushing a
        // stick). Devices that already carry analog input (sticks) pass through untouched.
        [Header("Keyboard -> stick")]
        [SerializeField] private float rampUpTime = 0.15f;                 // how long to full strength
        [SerializeField] private float rampDownTime = 0.1f;                // recenter time after release
        [SerializeField] private AnimationCurve stickCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        // Virtual stick position (-1..1). Reversing direction passes through 0 via MoveTowards naturally;
        // the flip across center needs no special-casing
        private Vector2 stick;

        /// <summary>
        /// awake all input system and setting before the game life loop start.
        /// </summary>
        void Awake()
        {
            EnsureActionsInitialized();

            // The GameManager survives scene switches via PersistentGameRoot's DontDestroyOnLoad, so a
            // held player reference goes stale after a change (a Unity fake-null that `?.` cannot
            // intercept) — always bind to the current scene's player.
            ResolvePlayer();
            // No warning when this leaves player null: the boot scene (the menu) legitimately has
            // none, and rooms with a runtime-spawned player bind later — sceneLoaded re-resolves,
            // and Update re-reads the bus every frame until the reference sticks
        }

        /// <summary>
        /// UIManager shares this GameObject and may call SetPlaying from its Awake before Unity has
        /// invoked ours. Keep action lookup lazy as well as doing it in Awake so component ordering
        /// can never leave the public input gate dereferencing null actions.
        /// </summary>
        private void EnsureActionsInitialized()
        {
            if (actionsInitialized) return;

            playerInput = InputActions.Wrapper;
            move = playerInput.Player.Move;
            aim = playerInput.Player.Aim;
            jump = playerInput.Player.Jump;
            dash = playerInput.Player.Dash;
            ropeFire = playerInput.Player.RopeFire;
            spitBomb = playerInput.Player.SpitBomb;
            interact = playerInput.Player.Interact;
            pressButtons = new[] { jump, dash, ropeFire, spitBomb, interact };

            actionsInitialized = true;
        }

        void OnDestroy()
        {
            // No Dispose here: the wrapper is the shared InputActions instance, releasing it would
            // kill it under every other consumer. It is static and ends with play mode.
            gameplayInputLockOwners.Clear();
        }

        // The persistent GameManager survives scene switches (PersistentGameRoot calls DontDestroyOnLoad
        // on its host), so a held player reference goes stale the moment the scene changes —
        // a destroyed UnityEngine.Object reads non-null to C# `?.`, letting the call chain run all the
        // way into a dead PlayerHandler/RopeGun (MissingReferenceException). sceneLoaded fires after the
        // new scene's objects are all instantiated, so re-binding here always finds the live player.
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) => ResolvePlayer();

        private void OnPlayerRegistered(PlayerHandler registered) => player = registered;

        private void ResolvePlayer()
        {
            // The bus holds the current scene's live player (registered by PlayerHandler on Awake);
            // a scene without a player (menu) leaves it null and the next sceneLoaded re-resolves
            player = PlayerBus.Player;
        }

        void Update()
        {
            // Scoped presentation owners normally release explicitly. Prune Unity fake-null entries
            // as a final guard so a destroyed map/cutscene can never lock gameplay permanently.
            if (gameplayInputLockOwners.RemoveWhere(owner => owner == null) > 0)
                ApplyActionState();

            AutoSwitchDevice();   // before filtering: the active family follows whichever device produced input

            // Paused (UIManager disabled the actions): stop forwarding entirely — the menu is in
            // charge, and ReadValue on a disabled action returns default which would push a stale
            // "no input" into PlayerHandler every frame.
            if (!actionsEnabled) return;

            // Runtime-spawned players bind here: PlayerBus.Player is only filled once RoomIntro's
            // entrance instantiates the cast, which can be seconds after sceneLoaded's one-shot
            // re-bind — so the reference is re-read until it sticks instead of staying null forever
            if (player == null) player = PlayerBus.Player;
            if (player == null) return;

            // Device filter: the Controls tab picks one input family; the other family's controls are
            // ignored so a gamepad left in the drawer cannot drive the player (and vice versa). The
            // move and aim axes are filtered independently — a stick drifting on a disabled family
            // must not bleed into an enabled keyboard, and vice versa.
            if (DeviceMatches(move.activeControl))
            {
                Vector2 raw = move.ReadValue<Vector2>();

                // Non-keyboard devices (gamepad sticks) already output analog values; pass through raw,
                // bypassing the keyboard synthesis
                InputControl control = move.activeControl;
                if (control != null && !(control.device is Keyboard))
                {
                    player.Move(raw);
                }
                else
                {
                    // Keyboard: each axis ramps up by press duration / recenters on release
                    stick.x = RampAxis(stick.x, raw.x, Time.deltaTime);
                    stick.y = RampAxis(stick.y, raw.y, Time.deltaTime);

                    player.Move(new Vector2(
                        Mathf.Sign(stick.x) * stickCurve.Evaluate(Mathf.Abs(stick.x)),
                        Mathf.Sign(stick.y) * stickCurve.Evaluate(Mathf.Abs(stick.y))));
                }
            }

            if (DeviceMatches(aim.activeControl))
            {
                // Aiming: the Aim action (mouse delta / right stick). Mouse deltas are in pixels, sticks
                // are analog — the pixelDelta flag distinguishes them, conversion happens in RopeGun
                InputControl aimControl = aim.activeControl;
                Vector2 aimValue = aim.ReadValue<Vector2>();
                player.Aim(aimValue, aimControl != null && aimControl.device is Mouse);
            }
        }

        /// <summary>
        /// Auto device switching: whichever family actually produced gameplay input becomes the active
        /// one, so a connected pad "just works" without visiting the Controls tab. Drift is ignored —
        /// only input past the noise threshold counts; a manual pick in settings still wins until the
        /// other family acts.
        /// </summary>
        private void AutoSwitchDevice()
        {
            if (move.activeControl != null && move.ReadValue<Vector2>().sqrMagnitude > 0.01f)
            { SwitchTo(move.activeControl.device); return; }
            if (aim.activeControl != null && aim.ReadValue<Vector2>().sqrMagnitude > 0.01f)
            { SwitchTo(aim.activeControl.device); return; }
            foreach (InputAction action in pressButtons)
                if (action.activeControl != null) { SwitchTo(action.activeControl.device); return; }
        }

        private static void SwitchTo(InputDevice device)
        {
            // The pad that just moved is the one haptics go to (only one pad ever rumbles)
            ActivePadTracker.Note(device);
            SettingsStore.InputDevice expected = device is Gamepad
                ? SettingsStore.InputDevice.Gamepad
                : SettingsStore.InputDevice.KeyboardMouse;
            if (SettingsStore.Device != expected) SettingsStore.SetDevice(expected);
        }

        /// <summary>True when the control's device family matches the selected input device; a null
        /// control (no active input) is always allowed through so nothing stalls.</summary>
        private static bool DeviceMatches(InputControl control)
        {
            if (control == null) return true;
            bool isGamepad = control.device is Gamepad;
            return SettingsStore.Device == SettingsStore.InputDevice.KeyboardMouse ? !isGamepad : isGamepad;
        }

        // Single-axis stick synthesis: moves toward the target (0 or ±1) at a constant rate.
        // MoveTowards needs a large rate for one-step arrival — here rate = 1/duration, full strength
        // reached in exactly rampUpTime seconds
        private float RampAxis(float current, float target, float dt)
        {
            float rate = Mathf.Approximately(target, 0f)
                ? (rampDownTime > 0f ? 1f / rampDownTime : 1f)
                : (rampUpTime > 0f ? 1f / rampUpTime : 1f);
            return Mathf.MoveTowards(current, target, rate * dt);
        }

        private void OnJumpPressed(InputAction.CallbackContext ctx)
        {
            if (!DeviceMatches(ctx.control)) return;
            player?.JumpPressed();
        }

        private void OnJumpReleased(InputAction.CallbackContext ctx)
        {
            if (!DeviceMatches(ctx.control)) return;
            player?.JumpReleased();
        }

        private void OnDash(InputAction.CallbackContext ctx)
        {
            if (!DeviceMatches(ctx.control)) return;
            player?.Dash();
        }

        private void OnRopeFire(InputAction.CallbackContext ctx)
        {
            if (!DeviceMatches(ctx.control)) return;
            player?.RopeFire();
        }

        private void OnSpitBomb(InputAction.CallbackContext ctx)
        {
            if (!DeviceMatches(ctx.control)) return;
            player?.SpitBomb();
        }

        // Confirm is not a player verb: it addresses whatever world part the player stands near
        // (AbilityPickupPart...), so it goes out on the bus instead of into PlayerHandler
        private void OnInteract(InputAction.CallbackContext ctx)
        {
            if (!DeviceMatches(ctx.control)) return;
            PlayerBus.RaiseInteractPressed();
        }

        void OnEnable()
        {
            // Re-resolve the player after every scene change: the persistent GameManager (kept alive by
            // PersistentGameRoot's DontDestroyOnLoad) must keep routing input to the current scene's
            // player, not the destroyed one from the scene it spawned in
            SceneManager.sceneLoaded += OnSceneLoaded;
            SettingsStore.Changed += OnSettingsChanged;
            PlayerBus.PlayerRegistered += OnPlayerRegistered;

            ApplyActionState();
        }

        void OnDisable()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SettingsStore.Changed -= OnSettingsChanged;
            PlayerBus.PlayerRegistered -= OnPlayerRegistered;

            DisableActions();
        }

        // Any settings change clears the synthesized stick. Overkill for most edits, but the one case
        // that matters — switching input device mid-run — must not leave a stale keyboard ramp that
        // fires the first time the new device is touched; zeroing is cheap and harmless otherwise.
        private void OnSettingsChanged()
        {
            stick = Vector2.zero;
        }

        /// <summary>
        /// Toggles gameplay input. Called by UIManager on pause/resume: the pause menu must not let
        /// move/jump/aim leak into the frozen game. Both EnableActions/DisableActions are idempotent
        /// (guarded by actionsEnabled) and own the event subscriptions, so pause→resume round-trips
        /// cannot double-subscribe the performed callbacks.
        /// </summary>
        public void SetPlaying(bool playing)
        {
            wantsActionsEnabled = playing;
            ApplyActionState();
        }

        /// <summary>
        /// Adds or removes a scoped gameplay-input lock without changing the flow layer's desired
        /// playing state. Locks stack by owner, so resuming a pause menu cannot unlock an active
        /// wall-map inspection; removing the final lock reapplies the latest SetPlaying request.
        /// </summary>
        public void SetGameplayInputLocked(Object owner, bool locked)
        {
            // ReferenceEquals lets an owner's OnDestroy remove its entry even after Unity has begun
            // reporting that object as fake-null.
            if (ReferenceEquals(owner, null)) return;

            bool changed = locked
                ? gameplayInputLockOwners.Add(owner)
                : gameplayInputLockOwners.Remove(owner);
            if (changed) ApplyActionState();
        }

        private void ApplyActionState()
        {
            if (!isActiveAndEnabled) return;
            if (wantsActionsEnabled && gameplayInputLockOwners.Count == 0) EnableActions();
            else DisableActions();
        }

        private void EnableActions()
        {
            EnsureActionsInitialized();
            if (actionsEnabled) return;
            actionsEnabled = true;

            // Enable one by one rather than playerInput.Player.Enable(): the map still holds
            // Crouch / Previous / Next — three actions this game does not use; enabling the whole
            // map would light them up too
            move.Enable();
            aim.Enable();
            jump.Enable();
            dash.Enable();
            ropeFire.Enable();
            spitBomb.Enable();
            interact.Enable();

            jump.performed += OnJumpPressed;
            jump.canceled += OnJumpReleased;
            dash.performed += OnDash;
            ropeFire.performed += OnRopeFire;
            spitBomb.performed += OnSpitBomb;
            interact.performed += OnInteract;
        }

        private void DisableActions()
        {
            if (!actionsEnabled) return;
            actionsEnabled = false;

            jump.performed -= OnJumpPressed;
            jump.canceled -= OnJumpReleased;
            dash.performed -= OnDash;
            ropeFire.performed -= OnRopeFire;
            spitBomb.performed -= OnSpitBomb;
            interact.performed -= OnInteract;

            move.Disable();
            aim.Disable();
            jump.Disable();
            dash.Disable();
            ropeFire.Disable();
            spitBomb.Disable();
            interact.Disable();
        }

        private bool actionsEnabled;
        private bool wantsActionsEnabled = true;
    }
}

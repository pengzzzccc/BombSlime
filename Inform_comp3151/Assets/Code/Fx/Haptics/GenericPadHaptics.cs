using Inkform.Bus;
using Inkform.Player;
using Inkform.Settings;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.DualShock;
using UnityEngine.InputSystem.XInput;

namespace Inkform.Fx.Haptics
{
    /// <summary>
    /// Rumble for every pad except the DualSense — Xbox (XInput), DualShock 4, Switch, generic —
    /// kept apart from HapticsDirector's mixer and written the plain Unity way:
    /// Gamepad.SetMotorSpeeds on the pad the player holds.
    ///
    /// One feel only — the whole pad shakes once — when the player touches a surface (ground,
    /// ceiling, either wall) or fires the rope gun; no trigger feel. Strength is Unity's
    /// documented example, SetMotorSpeeds(0.25f, 0.75f), at High; Medium and Low scale it down in
    /// the DualSense's proportions. Hits that land during a shake extend it rather than stacking.
    ///
    /// The menus get one short shake of their own: a value pushed past its end in the settings
    /// (UiBus.Bumped). Unlike the gameplay shake it plays in the menus and through a pause.
    ///
    /// Lives on the GameManager next to HapticsDirector, which drives the DualSense only.
    /// </summary>
    public class GenericPadHaptics : MonoBehaviour
    {
        [Header("Shake (Unity's SetMotorSpeeds example: low 0.25, high 0.75)")]
        [Tooltip("Left motor: low frequency, heavy")]
        [SerializeField, Range(0f, 1f)] private float lowSpeed = 0.25f;
        [Tooltip("Right motor: high frequency, light")]
        [SerializeField, Range(0f, 1f)] private float highSpeed = 0.75f;
        [Tooltip("Share of the shake above at Low / Medium / High")]
        [SerializeField] private float[] levelScales = { 0.35f, 0.65f, 1f };
        [SerializeField] private float shakeSeconds = 0.15f;
        [Tooltip("Menus: a value pushed past its end")]
        [SerializeField] private float bumpSeconds = 0.1f;

        [Header("Contact")]
        [Tooltip("Touches slower than this (into the surface) do not rumble: sliding along a wall, a small step")]
        [SerializeField] private float minImpactSpeed = 1.5f;

        [Header("Debug (read-only, refreshed in Play mode)")]
        [SerializeField] private string debugPad;
        [SerializeField] private float debugLow;
        [SerializeField] private float debugHigh;
        [SerializeField] private bool debugDriving;

        private Gamepad target;
        private float shakeUntil = float.NegativeInfinity;
        private float bumpUntil = float.NegativeInfinity;
        private float sentLow, sentHigh;
        private bool hasFocus = true;

        void OnEnable()
        {
            PlayerBus.Contacted += OnContact;
            RopeGunBus.Fired += OnRopeFired;
            UiBus.Bumped += OnBumped;
        }

        void OnDisable()
        {
            PlayerBus.Contacted -= OnContact;
            RopeGunBus.Fired -= OnRopeFired;
            UiBus.Bumped -= OnBumped;
            StopAll();
        }

        void OnApplicationQuit() => StopAll();

        void OnApplicationFocus(bool focus)
        {
            hasFocus = focus;
            if (!focus) StopAll();
        }

        private static bool Playing => GameStateStore.Current == GameStateStore.GameState.Playing;

        // ---- Events ----

        private void OnContact(ContactSide side, float impactSpeed)
        {
            if (impactSpeed >= minImpactSpeed) Shake();
        }

        private void OnRopeFired(Vector2 direction) => Shake();

        private void Shake()
        {
            if (!Playing) return;
            shakeUntil = Mathf.Max(shakeUntil, Time.unscaledTime + shakeSeconds);
        }

        // Menu feedback: any game state (main menu, pause), so no Playing check
        private void OnBumped() => bumpUntil = Mathf.Max(bumpUntil, Time.unscaledTime + bumpSeconds);

        // ---- Per frame ----

        void Update()
        {
            Gamepad next = SettingsStore.Rumble && hasFocus ? RumbleTarget(ActivePadTracker.Active) : null;
            if (next != target)
            {
                // Switched controllers (or rumble off): stop the one let go of first
                Write(0f, 0f);
                target = next;
                sentLow = sentHigh = -1f;
            }

            debugDriving = target != null;
            debugPad = target != null ? $"{target.displayName} ({target.layout})" : "(none)";
            if (target == null) return;

            // Pause, menus, transitions: stop the gameplay shake at once (the menus' bump plays on)
            if (!Playing) shakeUntil = float.NegativeInfinity;

            float now = Time.unscaledTime;
            float low = 0f, high = 0f;
            if (now < shakeUntil || now < bumpUntil)
                Speeds(SettingsStore.RumbleLevel, lowSpeed, highSpeed, levelScales, out low, out high);
            Write(low, high);
        }

        // SetMotorSpeeds only on change: the motors hold their speed until told otherwise
        private void Write(float low, float high)
        {
            if (target == null || !target.added) return;
            if (Mathf.Approximately(low, sentLow) && Mathf.Approximately(high, sentHigh)) return;

            target.SetMotorSpeeds(low, high);
            sentLow = debugLow = low;
            sentHigh = debugHigh = high;
        }

        private void StopAll()
        {
            shakeUntil = float.NegativeInfinity;
            bumpUntil = float.NegativeInfinity;
            Write(0f, 0f);
        }

        /// <summary>The device that takes rumble for this pad, or null for a DualSense
        /// (HapticsDirector's). Some connections (Bluetooth) also show an Xbox controller as a
        /// Microsoft HID device, which takes no rumble: the XInput device object stands in for it.</summary>
        private static Gamepad RumbleTarget(Gamepad pad)
        {
            if (pad == null || pad is DualSenseGamepadHID) return null;
            if (pad is XInputController || !IsMicrosoftHid(pad)) return pad;
            foreach (Gamepad other in Gamepad.all)
                if (other is XInputController && other.added) return other;
            return null;
        }

        private static bool IsMicrosoftHid(Gamepad pad)
        {
            string caps = pad.description.capabilities;
            return pad.description.interfaceName == "HID" && !string.IsNullOrEmpty(caps)
                && (caps.Contains("\"vendorId\":1118") || caps.Contains("\"vendorId\": 1118"));   // 0x045E Microsoft
        }

        /// <summary>Motor speeds of the shake at a rumble level: the shake scaled by that level's
        /// share; Off (or a level without a share) is silence.</summary>
        public static void Speeds(RumbleLevel level, float lowSpeed, float highSpeed, float[] scales,
            out float low, out float high)
        {
            int index = (int)level - 1;
            float k = scales != null && index >= 0 && index < scales.Length ? Mathf.Clamp01(scales[index]) : 0f;
            low = Mathf.Clamp01(lowSpeed * k);
            high = Mathf.Clamp01(highSpeed * k);
        }
    }
}

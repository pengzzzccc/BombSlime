using System;
using Inkform.Settings;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Utilities;

namespace Inkform.Fx.Haptics
{
    /// <summary>
    /// The pad the player is holding: the last gamepad that produced deliberate input. Haptics go
    /// to this pad only, so with several pads connected the idle ones stay still.
    ///
    /// Fed from two places: any gamepad button press (InputSystem.onAnyButtonPress, which also
    /// covers menus), and InputHandler's device auto-switch, which already filters stick and
    /// d-pad movement by a noise threshold. With the keyboard family selected there is no active
    /// pad — a pad lying on the desk must not buzz while the player types — after a short grace:
    /// a stray mouse nudge mid-play flips the family for a moment, and the pad in the player's
    /// hands must not drop out (and cut a rumble in flight) because of it.
    /// </summary>
    public static class ActivePadTracker
    {
        private const float KeyboardGraceSeconds = 1.5f;

        private static Gamepad noted;
        private static IDisposable buttonListener;
        private static SettingsStore.InputDevice? lastFamily;   // null until first read: starting on the keyboard is no switch
        private static float keyboardSince = float.NegativeInfinity;

        /// <summary>The active pad (still connected), or null.</summary>
        public static Gamepad Active
        {
            get
            {
                SettingsStore.InputDevice family = SettingsStore.Device;
                if (lastFamily.HasValue && family != lastFamily.Value && family != SettingsStore.InputDevice.Gamepad)
                    keyboardSince = Time.unscaledTime;
                lastFamily = family;
                if (family != SettingsStore.InputDevice.Gamepad
                    && Time.unscaledTime - keyboardSince >= KeyboardGraceSeconds) return null;

                if (noted != null && noted.added) return noted;

                // Nothing noted yet this session (the pad has only moved its sticks in a menu):
                // the system's current pad is the best guess
                Gamepad current = Gamepad.current;
                return current != null && current.added ? current : null;
            }
        }

        /// <summary>A device produced deliberate input; pads become the active one.</summary>
        public static void Note(InputDevice device)
        {
            if (device is Gamepad pad && pad.added) noted = pad;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            buttonListener?.Dispose();
            buttonListener = null;
            noted = null;
            lastFamily = null;
            keyboardSince = float.NegativeInfinity;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            buttonListener?.Dispose();
            buttonListener = InputSystem.onAnyButtonPress.Call(control => Note(control.device));
            InputSystem.onDeviceChange -= OnDeviceChange;
            InputSystem.onDeviceChange += OnDeviceChange;
        }

        private static void OnDeviceChange(InputDevice device, InputDeviceChange change)
        {
            if (device == noted && (change == InputDeviceChange.Disconnected || change == InputDeviceChange.Removed))
                noted = null;
        }
    }
}

using UnityEngine.InputSystem;
using UnityEngine.InputSystem.DualShock;

namespace Inkform.Fx.Haptics
{
    /// <summary>
    /// One physical pad's haptic output. The director mixes "what should be felt" into two motor
    /// levels and a trigger effect per side every frame; each implementation renders that on its
    /// hardware: DualSenseHaptics writes motors and adaptive triggers in one HID report.
    /// Every other pad is not driven through here at all — GenericPadHaptics owns them.
    /// </summary>
    public interface IPadHaptics
    {
        Gamepad Pad { get; }

        /// <summary>True when the pad renders trigger effects in the triggers themselves.</summary>
        bool NativeTriggers { get; }

        /// <summary>How mixed levels map onto this pad's motors at the current rumble level
        /// (set by the director every frame).</summary>
        MotorCurve Curve { set; }

        /// <summary>True when this output already drives that pad's motors. The director keeps
        /// the output instead of silencing and rebuilding it.</summary>
        bool Covers(Gamepad pad);

        // ---- Diagnostics (the director's Debug readout) ----
        string Route { get; }
        float SentLow { get; }
        float SentHigh { get; }
        bool LastWriteOk { get; }

        /// <summary>Called once per frame with the mixed motor levels (0..1) and the effect each
        /// trigger should hold. Implementations write to the device only when something changed.</summary>
        void Apply(float low, float high, in TriggerEffect left, in TriggerEffect right, float deltaTime);

        /// <summary>Motors to zero, triggers to Off — before switching pads, on pause/focus loss,
        /// and on quit (an adaptive-trigger effect otherwise outlives the game). False = the pad
        /// refused the write this frame (busy); call again.</summary>
        bool Silence();
    }

    /// <summary>
    /// Mixed level (0..1, tuned on the DualSense) to motor output for one pad family at one rumble
    /// level: scaled, then lifted to at least <see cref="floor"/> (eccentric motors do not spin
    /// up below roughly 0.1, so short taps would otherwise vanish).
    /// </summary>
    [System.Serializable]
    public struct MotorCurve
    {
        public float scale;
        public float floor;

        public MotorCurve(float scale, float floor)
        {
            this.scale = scale;
            this.floor = floor;
        }

        public static MotorCurve Identity => new MotorCurve(1f, 0f);

        public float Apply(float level)
        {
            if (level <= 0.001f) return 0f;
            float v = level * scale;
            if (v < floor) v = floor;
            return v > 1f ? 1f : v;
        }
    }

    public static class PadHapticsFactory
    {
        /// <summary>DualSense (and Edge) get the native report writer. Every other pad gets none
        /// here — GenericPadHaptics shakes them on its own.</summary>
        public static IPadHaptics For(Gamepad pad) =>
            pad is DualSenseGamepadHID dualSense ? new DualSenseHaptics(dualSense) : null;
    }
}

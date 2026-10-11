namespace Inkform.Fx.Haptics
{
    /// <summary>
    /// One trigger's force-feedback state, described independently of any pad. DualSense encodes
    /// it into its adaptive-trigger block (DualSenseReport); other pads have no trigger motors and
    /// get no trigger feel (GenericPadHaptics only shakes the whole pad).
    ///
    /// Positions are the DualSense's ten trigger zones: 0 = released, 9 = fully pulled.
    /// Strengths / amplitudes run 1..8 (0 = nothing).
    /// </summary>
    public readonly struct TriggerEffect
    {
        public enum Kind : byte
        {
            Off,
            /// <summary>Constant resistance from Start to the end of travel.</summary>
            Resistance,
            /// <summary>Elastic: resistance grows zone by zone from Start (1) to the end (Strength).</summary>
            Spring,
            /// <summary>Resistance from Start up to End, where it breaks and the rest is free.</summary>
            Weapon,
            /// <summary>Like Weapon, plus a snap-back force (SnapForce) pushing the trigger home.</summary>
            Bow,
            /// <summary>The trigger buzzes from Start on: Strength = amplitude, Frequency in Hz.</summary>
            Vibration
        }

        public readonly Kind kind;
        public readonly byte start;
        public readonly byte end;
        public readonly byte strength;
        public readonly byte snapForce;
        public readonly byte frequency;

        private TriggerEffect(Kind kind, int start, int end, int strength, int snapForce, int frequency)
        {
            this.kind = kind;
            this.start = Clamp(start, 0, 9);
            this.end = Clamp(end, 0, 9);
            this.strength = Clamp(strength, 0, 8);
            this.snapForce = Clamp(snapForce, 0, 8);
            this.frequency = Clamp(frequency, 0, 255);
        }

        public static TriggerEffect Off => default;

        public static TriggerEffect Resistance(int start, int strength) =>
            new TriggerEffect(Kind.Resistance, start, 9, strength, 0, 0);

        public static TriggerEffect Spring(int start, int endStrength) =>
            new TriggerEffect(Kind.Spring, start, 9, endStrength, 0, 0);

        public static TriggerEffect Weapon(int start, int breakAt, int strength) =>
            new TriggerEffect(Kind.Weapon, start, breakAt, strength, 0, 0);

        public static TriggerEffect Bow(int start, int breakAt, int strength, int snapForce) =>
            new TriggerEffect(Kind.Bow, start, breakAt, strength, snapForce, 0);

        public static TriggerEffect Vibration(int start, int amplitude, int frequency) =>
            new TriggerEffect(Kind.Vibration, start, 9, amplitude, 0, frequency);

        public bool IsOff => kind == Kind.Off || strength == 0;

        /// <summary>The same effect at a share of its strength (the player's Trigger Effects
        /// level): resistance / amplitude and snap-back scale and round, but a felt effect never
        /// drops below 1 — a lower level is lighter, not gone. Zones and frequency stay.</summary>
        public TriggerEffect Scaled(float share)
        {
            if (IsOff || share >= 1f) return this;
            return new TriggerEffect(kind, start, end, ScaleStep(strength, share), ScaleStep(snapForce, share), frequency);
        }

        private static int ScaleStep(byte value, float share)
        {
            if (value == 0) return 0;
            int scaled = (int)System.Math.Round(value * (double)share, System.MidpointRounding.AwayFromZero);
            return scaled < 1 ? 1 : scaled;
        }

        /// <summary>Zone position as a fraction of trigger travel (what Gamepad.*Trigger reads).</summary>
        public static float ZoneToTravel(int zone) => zone / 9f;

        public bool Equals(in TriggerEffect other) =>
            kind == other.kind && start == other.start && end == other.end && strength == other.strength
            && snapForce == other.snapForce && frequency == other.frequency;

        private static byte Clamp(int value, int min, int max) => (byte)(value < min ? min : value > max ? max : value);
    }
}

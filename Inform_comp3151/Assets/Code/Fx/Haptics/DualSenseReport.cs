using System;

namespace Inkform.Fx.Haptics
{
    /// <summary>
    /// Pure byte-level encoding of the DualSense output report: the two compatibility rumble
    /// motors and both adaptive-trigger blocks, for USB (report 0x02) and Bluetooth (report 0x31
    /// with its sequence tag and CRC32). No device access here — DualSenseHaptics sends the bytes.
    ///
    /// Layout of the shared 47-byte payload ("common" block, offsets relative to its start):
    ///   0  valid flag 0  (0x01 rumble emulation, 0x02 rumble instead of haptics,
    ///                     0x04 right trigger block valid, 0x08 left trigger block valid)
    ///   1  valid flag 1  (lightbar / LEDs — never set: the lightbar stays as the system left it)
    ///   2  right motor (high frequency)     3  left motor (low frequency)
    ///  10  right trigger effect, 11 bytes   21  left trigger effect, 11 bytes
    /// USB: [0] = 0x02, payload at [1]. Bluetooth: [0] = 0x31, [1] = seq &lt;&lt; 4, [2] = 0x10,
    /// payload at [3], CRC32 of (0xA2 + bytes 0..73) little-endian at [74..77].
    ///
    /// Trigger effect encodings follow the publicly documented DualSense effect modes
    /// (Feedback 0x21, Bow 0x22, Weapon 0x25, Vibration 0x26, Off 0x05).
    /// </summary>
    public static class DualSenseReport
    {
        public const int UsbLength = 48;
        public const int BluetoothLength = 78;
        public const int EffectLength = 11;

        public const byte FlagRumble = 0x01 | 0x02;
        public const byte FlagRightTrigger = 0x04;
        public const byte FlagLeftTrigger = 0x08;

        private const int MotorRightOffset = 2;
        private const int MotorLeftOffset = 3;
        private const int RightTriggerOffset = 10;
        private const int LeftTriggerOffset = 21;

        private const byte ModeOff = 0x05;
        private const byte ModeFeedback = 0x21;
        private const byte ModeBow = 0x22;
        private const byte ModeWeapon = 0x25;
        private const byte ModeVibration = 0x26;

        /// <summary>Fills <paramref name="report"/> (cleared first) with one output report.
        /// flags picks which blocks the pad applies; a block whose flag is clear is ignored by
        /// the pad, so an unchanged trigger is not re-armed mid-pull.</summary>
        public static int Build(byte[] report, bool bluetooth, byte sequence, byte flags,
            float lowMotor, float highMotor, in TriggerEffect left, in TriggerEffect right)
        {
            int length = bluetooth ? BluetoothLength : UsbLength;
            Array.Clear(report, 0, report.Length);

            int payload;
            if (bluetooth)
            {
                report[0] = 0x31;
                report[1] = (byte)((sequence & 0x0F) << 4);
                report[2] = 0x10;
                payload = 3;
            }
            else
            {
                report[0] = 0x02;
                payload = 1;
            }

            report[payload] = flags;
            report[payload + MotorRightOffset] = ToByte(highMotor);
            report[payload + MotorLeftOffset] = ToByte(lowMotor);
            EncodeEffect(right, report, payload + RightTriggerOffset);
            EncodeEffect(left, report, payload + LeftTriggerOffset);

            if (bluetooth)
            {
                uint crc = Crc32(report, 0, BluetoothLength - 4, seed: 0xA2);
                report[74] = (byte)crc;
                report[75] = (byte)(crc >> 8);
                report[76] = (byte)(crc >> 16);
                report[77] = (byte)(crc >> 24);
            }
            return length;
        }

        /// <summary>Writes one 11-byte adaptive-trigger block. Out-of-range parameters fall back
        /// to Off rather than sending the pad something it would misread.</summary>
        public static void EncodeEffect(in TriggerEffect effect, byte[] dst, int offset)
        {
            Array.Clear(dst, offset, EffectLength);
            switch (effect.kind)
            {
                case TriggerEffect.Kind.Resistance:
                    if (effect.strength > 0)
                    {
                        WriteZones(dst, offset, ModeFeedback, effect.start, effect.strength, effect.strength);
                        return;
                    }
                    break;

                case TriggerEffect.Kind.Spring:
                    if (effect.strength > 0)
                    {
                        // Linear ramp: 1 at the start zone up to the requested strength at zone 9
                        WriteZones(dst, offset, ModeFeedback, effect.start, 1, effect.strength);
                        return;
                    }
                    break;

                case TriggerEffect.Kind.Weapon:
                    if (effect.strength > 0 && effect.start >= 2 && effect.start <= 7
                        && effect.end > effect.start && effect.end <= 8)
                    {
                        int zones = (1 << effect.start) | (1 << effect.end);
                        dst[offset] = ModeWeapon;
                        dst[offset + 1] = (byte)zones;
                        dst[offset + 2] = (byte)(zones >> 8);
                        dst[offset + 3] = (byte)(effect.strength - 1);
                        return;
                    }
                    break;

                case TriggerEffect.Kind.Bow:
                    if (effect.strength > 0 && effect.snapForce > 0
                        && effect.end > effect.start && effect.end <= 8)
                    {
                        int zones = (1 << effect.start) | (1 << effect.end);
                        int forcePair = ((effect.strength - 1) & 0x07) | (((effect.snapForce - 1) & 0x07) << 3);
                        dst[offset] = ModeBow;
                        dst[offset + 1] = (byte)zones;
                        dst[offset + 2] = (byte)(zones >> 8);
                        dst[offset + 3] = (byte)forcePair;
                        dst[offset + 4] = (byte)(forcePair >> 8);
                        return;
                    }
                    break;

                case TriggerEffect.Kind.Vibration:
                    if (effect.strength > 0 && effect.frequency > 0)
                    {
                        WriteZones(dst, offset, ModeVibration, effect.start, effect.strength, effect.strength);
                        dst[offset + 7] = 0;
                        dst[offset + 8] = 0;
                        dst[offset + 9] = effect.frequency;
                        return;
                    }
                    break;
            }
            dst[offset] = ModeOff;
        }

        // Feedback / Vibration share this shape: an active-zone bitmap, then 3 bits of strength
        // (value - 1) per zone packed into a 32-bit little-endian field. Strength runs linearly
        // from startStrength at the start zone to endStrength at zone 9 (equal = constant)
        private static void WriteZones(byte[] dst, int offset, byte mode, int start, int startStrength, int endStrength)
        {
            uint forces = 0;
            int active = 0;
            int span = Math.Max(1, 9 - start);
            for (int i = start; i < 10; i++)
            {
                int s = startStrength + (endStrength - startStrength) * (i - start) / span;
                s = Math.Max(1, Math.Min(8, s));
                forces |= (uint)((s - 1) & 0x07) << (3 * i);
                active |= 1 << i;
            }
            dst[offset] = mode;
            dst[offset + 1] = (byte)active;
            dst[offset + 2] = (byte)(active >> 8);
            dst[offset + 3] = (byte)forces;
            dst[offset + 4] = (byte)(forces >> 8);
            dst[offset + 5] = (byte)(forces >> 16);
            dst[offset + 6] = (byte)(forces >> 24);
        }

        private static byte ToByte(float value) =>
            (byte)Math.Round(Math.Max(0f, Math.Min(1f, value)) * 255f);

        // ---- CRC32 (IEEE 802.3, reflected 0xEDB88320) ----

        private static uint[] crcTable;

        /// <summary>Standard CRC32; seed is an optional byte hashed before the data (the
        /// DualSense Bluetooth output report hashes 0xA2, the HID "output" header, first).</summary>
        public static uint Crc32(byte[] data, int offset, int count, int seed = -1)
        {
            uint[] table = crcTable ??= BuildTable();
            uint crc = 0xFFFFFFFFu;
            if (seed >= 0) crc = table[(crc ^ (byte)seed) & 0xFF] ^ (crc >> 8);
            for (int i = offset; i < offset + count; i++)
                crc = table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return ~crc;
        }

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }
    }
}

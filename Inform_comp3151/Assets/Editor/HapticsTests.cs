#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Text;
using Inkform.Fx.Haptics;
using Inkform.Player;
using Inkform.Settings;
using NUnit.Framework;
using UnityEngine;

namespace Inkform.Tests
{
    /// <summary>
    /// Haptics pure logic: the DualSense adaptive-trigger encodings and output report layout
    /// (USB / Bluetooth + CRC32), the motor mixer's envelopes and freeze behaviour, the rumble
    /// level curves and their settings migration, and the player's contact edges. Device output
    /// itself needs a real pad (see the manual checks).
    /// </summary>
    public sealed class HapticsTests
    {
        private static byte[] Encode(TriggerEffect effect)
        {
            var block = new byte[DualSenseReport.EffectLength];
            DualSenseReport.EncodeEffect(effect, block, 0);
            return block;
        }

        // ---- Trigger effect blocks ----

        [Test]
        public void Off_IsMode05()
        {
            CollectionAssert.AreEqual(new byte[] { 0x05, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, Encode(TriggerEffect.Off));
        }

        [Test]
        public void Weapon_EncodesStartBreakBitmapAndStrength()
        {
            CollectionAssert.AreEqual(new byte[] { 0x25, 0x24, 0x00, 0x07, 0, 0, 0, 0, 0, 0, 0 },
                Encode(TriggerEffect.Weapon(2, 5, 8)));
        }

        [Test]
        public void Weapon_OutOfRange_FallsBackToOff()
        {
            Assert.AreEqual(0x05, Encode(TriggerEffect.Weapon(1, 5, 8))[0], "start below 2");
            Assert.AreEqual(0x05, Encode(TriggerEffect.Weapon(5, 5, 8))[0], "break not after start");
        }

        [Test]
        public void Bow_PacksStrengthAndSnapForce()
        {
            // forcePair = (6-1) | ((5-1) << 3) = 0x25
            CollectionAssert.AreEqual(new byte[] { 0x22, 0x24, 0x00, 0x25, 0x00, 0, 0, 0, 0, 0, 0 },
                Encode(TriggerEffect.Bow(2, 5, 6, 5)));
        }

        [Test]
        public void Resistance_FullTravelMaxStrength()
        {
            CollectionAssert.AreEqual(new byte[] { 0x21, 0xFF, 0x03, 0xFF, 0xFF, 0xFF, 0x3F, 0, 0, 0, 0 },
                Encode(TriggerEffect.Resistance(0, 8)));
        }

        [Test]
        public void Vibration_PacksAmplitudeZonesAndFrequency()
        {
            // amplitude 4 -> 3 per zone, ten zones: 0x1B6DB6DB little-endian
            CollectionAssert.AreEqual(new byte[] { 0x26, 0xFF, 0x03, 0xDB, 0xB6, 0x6D, 0x1B, 0, 0, 60, 0 },
                Encode(TriggerEffect.Vibration(0, 4, 60)));
        }

        [Test]
        public void Spring_StiffensZoneByZone()
        {
            byte[] block = Encode(TriggerEffect.Spring(1, 7));
            Assert.AreEqual(0x21, block[0]);
            Assert.AreEqual(0x3FE, block[1] | (block[2] << 8), "zones 1..9 active");

            uint forces = (uint)(block[3] | (block[4] << 8) | (block[5] << 16) | (block[6] << 24));
            int previous = 0;
            for (int zone = 1; zone <= 9; zone++)
            {
                int strength = (int)((forces >> (3 * zone)) & 0x7) + 1;
                Assert.GreaterOrEqual(strength, previous, $"zone {zone} softer than the one before");
                previous = strength;
            }
            Assert.AreEqual(7, previous, "full pull reaches the requested strength");
        }

        // ---- Output report ----

        [Test]
        public void Crc32_MatchesTheStandardCheckValue()
        {
            byte[] data = Encoding.ASCII.GetBytes("123456789");
            Assert.AreEqual(0xCBF43926u, DualSenseReport.Crc32(data, 0, data.Length));
        }

        [Test]
        public void UsbReport_Layout()
        {
            var report = new byte[DualSenseReport.BluetoothLength];
            byte flags = DualSenseReport.FlagRumble | DualSenseReport.FlagLeftTrigger | DualSenseReport.FlagRightTrigger;
            int length = DualSenseReport.Build(report, false, 0, flags, 1f, 0.5f,
                TriggerEffect.Weapon(2, 5, 8), TriggerEffect.Bow(2, 5, 6, 5));

            Assert.AreEqual(48, length);
            Assert.AreEqual(0x02, report[0]);
            Assert.AreEqual(0x0F, report[1]);
            Assert.AreEqual(0x00, report[2], "lightbar / LED flags stay clear");
            Assert.AreEqual(128, report[3], "right = high-frequency motor");
            Assert.AreEqual(255, report[4], "left = low-frequency motor");
            Assert.AreEqual(0x22, report[11], "right trigger block");
            Assert.AreEqual(0x25, report[22], "left trigger block");
        }

        [Test]
        public void BluetoothReport_LayoutAndCrc()
        {
            var report = new byte[DualSenseReport.BluetoothLength];
            int length = DualSenseReport.Build(report, true, 3, DualSenseReport.FlagRumble, 0.2f, 0.4f,
                TriggerEffect.Off, TriggerEffect.Off);

            Assert.AreEqual(78, length);
            Assert.AreEqual(0x31, report[0]);
            Assert.AreEqual(0x30, report[1], "sequence in the high nibble");
            Assert.AreEqual(0x10, report[2]);
            Assert.AreEqual(DualSenseReport.FlagRumble, report[3], "payload starts at [3]");
            Assert.AreEqual(0x05, report[3 + 10], "right trigger block");
            Assert.AreEqual(0x05, report[3 + 21], "left trigger block");

            uint crc = DualSenseReport.Crc32(report, 0, 74, seed: 0xA2);
            uint stored = (uint)(report[74] | (report[75] << 8) | (report[76] << 16) | (report[77] << 24));
            Assert.AreEqual(crc, stored);
        }

        // ---- Mixer ----

        [Test]
        public void Mixer_MaxMixesPerMotor()
        {
            var mixer = new HapticMixer();
            mixer.Add(0.2f, 0.1f, 1f);
            mixer.Add(0.1f, 0.3f, 1f);
            mixer.Tick(0.1f, out float low, out float high);
            Assert.AreEqual(0.2f, low, 1e-5f);
            Assert.AreEqual(0.3f, high, 1e-5f);
        }

        [Test]
        public void Mixer_EnvelopeInterpolates()
        {
            var mixer = new HapticMixer();
            mixer.Add(1f, 0f, 0f, 0.4f, 1f);
            mixer.Tick(0.25f, out float low, out float high);
            Assert.AreEqual(0.75f, low, 1e-4f);
            Assert.AreEqual(0.1f, high, 1e-4f);
        }

        [Test]
        public void Mixer_FreezeKeepsRemainingTime()
        {
            var mixer = new HapticMixer();
            mixer.Add(0.3f, 0f, 0.1f);
            for (int i = 0; i < 100; i++) mixer.Tick(0f, out _, out _);
            Assert.AreEqual(1, mixer.Count, "a frozen clip never expires");

            mixer.Tick(0.2f, out float low, out _);
            Assert.AreEqual(0, mixer.Count);
            Assert.AreEqual(0f, low);
        }

        [Test]
        public void Mixer_DelayHoldsTheClipBack()
        {
            var mixer = new HapticMixer();
            mixer.Add(0.5f, 0f, 0.1f, delay: 0.2f);
            mixer.Tick(0.1f, out float before, out _);
            Assert.AreEqual(0f, before);
            mixer.Tick(0.15f, out float after, out _);
            Assert.AreEqual(0.5f, after, 1e-5f);
        }

        // ---- Rumble levels ----

        [Test]
        public void MotorCurve_SilenceStaysSilent()
        {
            Assert.AreEqual(0f, new MotorCurve(2.8f, 0.15f).Apply(0f));
        }

        [Test]
        public void MotorCurve_LiftsSmallLevelsToTheFloorAndClampsLargeOnes()
        {
            var curve = new MotorCurve(2.8f, 0.15f);
            Assert.AreEqual(0.15f, curve.Apply(0.02f), 1e-5f, "a faint tap still spins the motor");
            Assert.AreEqual(0.56f, curve.Apply(0.2f), 1e-5f);
            Assert.AreEqual(1f, curve.Apply(0.9f), "never past full power");
        }

        [Test]
        public void RumbleLevel_MigratesTheOldSwitch()
        {
            Assert.AreEqual(RumbleLevel.Off, SettingsStore.MigrateRumbleLevel(false, 0, true, 0));
            Assert.AreEqual(RumbleLevel.High, SettingsStore.MigrateRumbleLevel(false, 0, true, 1));
            Assert.AreEqual(SettingsStore.DefaultLevel, SettingsStore.MigrateRumbleLevel(false, 0, false, 0), "fresh install");
            Assert.AreEqual(RumbleLevel.Medium, SettingsStore.MigrateRumbleLevel(true, (int)RumbleLevel.Medium, true, 1),
                "the levels key wins over the old switch");
            Assert.AreEqual(RumbleLevel.High, SettingsStore.MigrateRumbleLevel(true, 99, false, 0), "out of range clamps");
        }

        [Test]
        public void TriggerEffect_ScalesWithTheTriggerLevel()
        {
            TriggerEffect bow = TriggerEffect.Bow(2, 5, 6, 5);

            TriggerEffect low = bow.Scaled(0.35f);
            Assert.AreEqual(TriggerEffect.Kind.Bow, low.kind);
            Assert.AreEqual(bow.start, low.start, "zones stay where they are");
            Assert.AreEqual(bow.end, low.end);
            Assert.AreEqual(2, low.strength);
            Assert.AreEqual(2, low.snapForce);

            TriggerEffect medium = bow.Scaled(0.65f);
            Assert.AreEqual(4, medium.strength);
            Assert.AreEqual(3, medium.snapForce);

            Assert.IsTrue(bow.Scaled(1f).Equals(bow), "High is the tuned feel as it is");

            TriggerEffect buzz = TriggerEffect.Vibration(0, 1, 160).Scaled(0.35f);
            Assert.AreEqual(1, buzz.strength, "a felt effect gets lighter, never gone");
            Assert.AreEqual(160, buzz.frequency, "the buzz keeps its pitch");
            Assert.IsTrue(TriggerEffect.Off.Scaled(0.35f).IsOff);
        }

        // ---- Generic pad shake ----

        [Test]
        public void GenericShake_LevelsScaleUnitysExample()
        {
            float[] scales = { 0.35f, 0.65f, 1f };

            GenericPadHaptics.Speeds(RumbleLevel.High, 0.25f, 0.75f, scales, out float low, out float high);
            Assert.AreEqual(0.25f, low, 1e-5f, "High is SetMotorSpeeds(0.25f, 0.75f) as documented");
            Assert.AreEqual(0.75f, high, 1e-5f);

            GenericPadHaptics.Speeds(RumbleLevel.Medium, 0.25f, 0.75f, scales, out low, out high);
            Assert.AreEqual(0.1625f, low, 1e-5f);
            Assert.AreEqual(0.4875f, high, 1e-5f);

            GenericPadHaptics.Speeds(RumbleLevel.Low, 0.25f, 0.75f, scales, out low, out high);
            Assert.AreEqual(0.0875f, low, 1e-5f);
            Assert.AreEqual(0.2625f, high, 1e-5f);

            GenericPadHaptics.Speeds(RumbleLevel.Off, 0.25f, 0.75f, scales, out low, out high);
            Assert.AreEqual(0f, low);
            Assert.AreEqual(0f, high);

            GenericPadHaptics.Speeds(RumbleLevel.High, 0.25f, 0.75f, new[] { 0.35f }, out low, out high);
            Assert.AreEqual(0f, high, "a level without a share is silence");
        }

        // ---- Contact edges ----

        private static List<(ContactSide side, float speed)> Detect(ContactEdges edges,
            bool ground, bool ceiling, bool left, bool right)
        {
            var hits = new List<(ContactSide, float)>();
            edges.Detect(ground, ceiling, left, right, (side, speed) => hits.Add((side, speed)));
            return hits;
        }

        [Test]
        public void Contact_LandingReportsDownWithLastFramesFallSpeed()
        {
            var edges = new ContactEdges();
            Detect(edges, false, false, false, false);
            edges.RememberVelocity(new Vector2(2f, -9f));

            var hits = Detect(edges, true, false, false, false);
            Assert.AreEqual(1, hits.Count);
            Assert.AreEqual(ContactSide.Down, hits[0].side);
            Assert.AreEqual(9f, hits[0].speed, 1e-5f);
        }

        [Test]
        public void Contact_StandingStillReportsNothingNew()
        {
            var edges = new ContactEdges();
            Detect(edges, true, false, false, false);
            edges.RememberVelocity(Vector2.zero);
            Assert.IsEmpty(Detect(edges, true, false, false, false));
        }

        [Test]
        public void Contact_CornerReportsEachSideOnce()
        {
            var edges = new ContactEdges();
            Detect(edges, false, false, false, false);
            edges.RememberVelocity(new Vector2(-6f, -4f));

            var hits = Detect(edges, true, false, true, false);
            Assert.AreEqual(2, hits.Count);
            Assert.AreEqual((ContactSide.Down, 4f), hits[0]);
            Assert.AreEqual((ContactSide.Left, 6f), hits[1]);
        }

        [Test]
        public void Contact_MovingAwayFromTheSurfaceHasNoImpactSpeed()
        {
            var edges = new ContactEdges();
            Detect(edges, false, false, false, false);
            edges.RememberVelocity(new Vector2(-3f, 0f));   // moving left while a right wall arrives
            var hits = Detect(edges, false, false, false, true);
            Assert.AreEqual(ContactSide.Right, hits[0].side);
            Assert.AreEqual(0f, hits[0].speed);
        }

        [Test]
        public void Contact_SyncBaselineSwallowsTheRespawnTouch()
        {
            var edges = new ContactEdges();
            Detect(edges, false, false, false, false);
            edges.RememberVelocity(new Vector2(0f, -20f));   // died mid-fall
            edges.SyncBaseline(true, false, false, false);   // respawned standing on the checkpoint
            Assert.IsEmpty(Detect(edges, true, false, false, false));
        }
    }
}
#endif

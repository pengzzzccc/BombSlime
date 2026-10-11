#if UNITY_INCLUDE_TESTS
using Inkform.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Inkform.Tests
{
    /// <summary>
    /// Menu input: the left stick's dead zone, direction and hold repeat (UiStick), and the
    /// option rows' end-of-range rules (OptionRow).
    /// </summary>
    public sealed class UiInputTests
    {
        private const NavigationMoveEvent.Direction None = NavigationMoveEvent.Direction.None;
        private const NavigationMoveEvent.Direction Up = NavigationMoveEvent.Direction.Up;
        private const NavigationMoveEvent.Direction Down = NavigationMoveEvent.Direction.Down;
        private const NavigationMoveEvent.Direction Left = NavigationMoveEvent.Direction.Left;
        private const NavigationMoveEvent.Direction Right = NavigationMoveEvent.Direction.Right;

        // ---- Stick ----

        [Test]
        public void Stick_DeadZoneIsRadialAt0375()
        {
            Assert.AreEqual(0.375f, UiStick.DeadZone);
            Assert.AreEqual(None, UiStick.Direction(new Vector2(0f, 0.37f), UiStick.DeadZone));
            Assert.AreEqual(Up, UiStick.Direction(new Vector2(0f, 0.38f), UiStick.DeadZone));
            // 0.3 on both axes is 0.42 off centre: past a radial dead zone (a per-axis one would miss it)
            Assert.AreNotEqual(None, UiStick.Direction(new Vector2(0.3f, -0.3f), UiStick.DeadZone));
        }

        [Test]
        public void Stick_DominantAxisPicksTheDirection()
        {
            Assert.AreEqual(Right, UiStick.Direction(new Vector2(0.8f, 0.3f), UiStick.DeadZone));
            Assert.AreEqual(Left, UiStick.Direction(new Vector2(-0.8f, -0.3f), UiStick.DeadZone));
            Assert.AreEqual(Down, UiStick.Direction(new Vector2(0.2f, -0.9f), UiStick.DeadZone));
            Assert.AreEqual(Up, UiStick.Direction(new Vector2(0.5f, 0.5f), UiStick.DeadZone), "a diagonal tie goes vertical");
        }

        [Test]
        public void Stick_DeadZoneRescalesToFullRange()
        {
            Assert.AreEqual(Vector2.zero, UiStick.ApplyDeadZone(new Vector2(0.3f, 0f), UiStick.DeadZone));
            Assert.AreEqual(0f, UiStick.ApplyDeadZone(new Vector2(0.375f, 0f), UiStick.DeadZone).x, 1e-4f);
            Assert.AreEqual(1f, UiStick.ApplyDeadZone(new Vector2(1f, 0f), UiStick.DeadZone).x, 1e-4f);
            Assert.AreEqual(-0.5f, UiStick.ApplyDeadZone(new Vector2(-0.6875f, 0f), UiStick.DeadZone).x, 1e-4f, "halfway out");
        }

        [Test]
        public void Stick_MovesOnThePushThenRepeatsWhileHeld()
        {
            var stick = new UiStick();
            Vector2 down = new Vector2(0f, -1f);

            Assert.AreEqual(Down, stick.Update(down, 0f), "the push moves at once");
            Assert.AreEqual(None, stick.Update(down, 0.2f), "no repeat before the first-repeat delay");
            Assert.AreEqual(Down, stick.Update(down, UiStick.FirstRepeatSeconds), "first repeat");
            Assert.AreEqual(None, stick.Update(down, UiStick.FirstRepeatSeconds + 0.05f));
            Assert.AreEqual(Down, stick.Update(down, UiStick.FirstRepeatSeconds + UiStick.RepeatSeconds), "steady repeat");

            Assert.AreEqual(None, stick.Update(Vector2.zero, 1f), "released");
            Assert.AreEqual(Down, stick.Update(down, 1.01f), "a new push moves at once again");
            Assert.AreEqual(Up, stick.Update(new Vector2(0f, 1f), 1.02f), "a new direction moves at once");
        }

        // ---- Option rows ----

        [Test]
        public void OptionRow_StepsStopAtTheEnds()
        {
            Assert.IsTrue(OptionRow.CanStep(0, +1, 2));
            Assert.IsFalse(OptionRow.CanStep(1, +1, 2), "ON pushed right: no wrap to OFF");
            Assert.IsFalse(OptionRow.CanStep(0, -1, 2), "OFF pushed left: no wrap to ON");
            Assert.IsTrue(OptionRow.CanStep(3, -1, 4));
            Assert.IsFalse(OptionRow.CanStep(0, +1, 1), "a single choice never steps");
        }

        [Test]
        public void OptionRow_ConfirmWrapsAround()
        {
            Assert.AreEqual(1, OptionRow.Wrap(1, 4));
            Assert.AreEqual(0, OptionRow.Wrap(4, 4), "past the last comes the first");
            Assert.AreEqual(3, OptionRow.Wrap(-1, 4));
            Assert.AreEqual(0, OptionRow.Wrap(5, 0), "no choices: stays at 0");
        }
    }
}
#endif

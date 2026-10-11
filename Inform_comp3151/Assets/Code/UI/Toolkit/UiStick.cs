using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace Inkform.UI
{
    /// <summary>
    /// Left-stick menu navigation, with the menus' own dead zone. The UI map's Navigate action
    /// keeps only the d-pad and the keyboard (the engine's stick threshold is fixed inside its
    /// input-for-UI layer); the stick is read here instead — a radial dead zone of
    /// <see cref="DeadZone"/>, the dominant axis picks the direction, one move on the push and
    /// then a repeat while held — and UIManager sends each move to the focused control as a
    /// NavigationMoveEvent, so focus moves and option rows step exactly as with the d-pad.
    ///
    /// The static helpers are pure (tested); the instance only keeps the repeat timing.
    /// </summary>
    public sealed class UiStick
    {
        /// <summary>Menu left-stick dead zone (share of full deflection).</summary>
        public const float DeadZone = 0.375f;

        /// <summary>Held: the first repeat comes after this long, then every RepeatSeconds.</summary>
        public const float FirstRepeatSeconds = 0.4f;
        public const float RepeatSeconds = 0.11f;

        private NavigationMoveEvent.Direction held = NavigationMoveEvent.Direction.None;
        private float nextAt;

        /// <summary>The move to send this frame (None most frames): the push itself, then the
        /// held repeats; a new direction starts over.</summary>
        public NavigationMoveEvent.Direction Update(Vector2 stick, float now)
        {
            NavigationMoveEvent.Direction dir = Direction(stick, DeadZone);
            if (dir == NavigationMoveEvent.Direction.None)
            {
                held = dir;
                return dir;
            }

            if (dir != held)
            {
                held = dir;
                nextAt = now + FirstRepeatSeconds;
                return dir;
            }

            if (now < nextAt) return NavigationMoveEvent.Direction.None;
            nextAt = now + RepeatSeconds;
            return dir;
        }

        /// <summary>Forget a held direction (no sheet on stage): the next push moves at once.</summary>
        public void Reset() => held = NavigationMoveEvent.Direction.None;

        // ---- Pure helpers ----

        /// <summary>Up / Down / Left / Right by the dominant axis, or None inside the radial dead
        /// zone. A diagonal tie goes to the vertical axis — lists are vertical.</summary>
        public static NavigationMoveEvent.Direction Direction(Vector2 stick, float deadZone)
        {
            if (stick.sqrMagnitude < deadZone * deadZone) return NavigationMoveEvent.Direction.None;
            if (Mathf.Abs(stick.x) > Mathf.Abs(stick.y))
                return stick.x > 0f ? NavigationMoveEvent.Direction.Right : NavigationMoveEvent.Direction.Left;
            return stick.y > 0f ? NavigationMoveEvent.Direction.Up : NavigationMoveEvent.Direction.Down;
        }

        /// <summary>The stick past a radial dead zone, rescaled so the edge of the dead zone reads 0
        /// and full deflection reads 1 (direction kept).</summary>
        public static Vector2 ApplyDeadZone(Vector2 stick, float deadZone)
        {
            float magnitude = stick.magnitude;
            if (magnitude < deadZone || magnitude <= 0f) return Vector2.zero;
            float scaled = Mathf.InverseLerp(deadZone, 1f, Mathf.Min(magnitude, 1f));
            return stick / magnitude * scaled;
        }

        /// <summary>The pad in hand's left stick past the menu dead zone (zero without a pad).</summary>
        public static Vector2 ReadLeft()
        {
            Gamepad pad = Gamepad.current;
            return pad != null ? ApplyDeadZone(pad.leftStick.ReadValue(), DeadZone) : Vector2.zero;
        }
    }
}

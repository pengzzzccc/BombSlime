using System;
using UnityEngine;

namespace Inkform.Player
{
    /// <summary>
    /// Turns ContactSensor's four per-frame flags into "just touched" moments: ground, ceiling,
    /// left wall, right wall, each with the speed the player was moving into that surface.
    ///
    /// The speed is the previous frame's velocity: by the frame the contact registers, the physics
    /// step has usually already stopped the body against the surface. Pure C# — PlayerHandler
    /// feeds it the flags after ContactSensor.Tick and the velocity after PlayerMotor.Tick.
    /// </summary>
    public sealed class ContactEdges
    {
        private bool ground, ceiling, leftWall, rightWall;
        private Vector2 lastVelocity;

        /// <summary>End-of-frame velocity, read as the impact speed on the next contact.</summary>
        public void RememberVelocity(Vector2 velocity) => lastVelocity = velocity;

        /// <summary>Adopts the current contacts without reporting them (respawn: standing on the
        /// checkpoint floor is not an impact).</summary>
        public void SyncBaseline(bool onGround, bool onCeiling, bool onLeftWall, bool onRightWall)
        {
            ground = onGround;
            ceiling = onCeiling;
            leftWall = onLeftWall;
            rightWall = onRightWall;
            lastVelocity = Vector2.zero;
        }

        /// <summary>Reports every side touched this frame that was free the frame before, with the
        /// speed into that side (0 when the player was not moving toward it).</summary>
        public void Detect(bool onGround, bool onCeiling, bool onLeftWall, bool onRightWall,
            Action<ContactSide, float> onContact)
        {
            if (onGround && !ground) onContact(ContactSide.Down, Mathf.Max(0f, -lastVelocity.y));
            if (onCeiling && !ceiling) onContact(ContactSide.Up, Mathf.Max(0f, lastVelocity.y));
            if (onLeftWall && !leftWall) onContact(ContactSide.Left, Mathf.Max(0f, -lastVelocity.x));
            if (onRightWall && !rightWall) onContact(ContactSide.Right, Mathf.Max(0f, lastVelocity.x));

            ground = onGround;
            ceiling = onCeiling;
            leftWall = onLeftWall;
            rightWall = onRightWall;
        }
    }
}

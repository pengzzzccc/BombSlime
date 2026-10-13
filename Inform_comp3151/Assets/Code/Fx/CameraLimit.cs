using UnityEngine;

namespace Inkform.Fx
{
    /// <summary>
    /// One-directional camera limit wall, finite and transform-driven: the wall segment runs along
    /// this object's own up axis, its centre is the object's position, and `length` is the wall's
    /// reach — rotate the object to slant the wall, move it to place the wall. The view is kept on
    /// one side of the wall line (blockPositive picks which, along the object's own right axis;
    /// the gizmo's chevrons point to that side) and never shows what lies beyond; past either end
    /// of the segment the camera follows freely. Multiple walls compose; a scene without any costs
    /// nothing. The line's coordinate is where the SCREEN edge stops — CamHandler offsets it by
    /// the view's half-size along the wall normal internally.
    ///
    /// A wall only acts while the player stands in its zone: a strip as long as the wall, reaching
    /// zoneDepth out on the kept side and zoneBehind back past the line — both tunable. Without
    /// the zone a short wall would grip a whole band of the map — a player anywhere beyond it, in
    /// another part of the level at the same height, would drag the camera back across. CamHandler
    /// fades a wall in and out as the player enters and leaves, so it never snaps.
    /// </summary>
    public class CameraLimit : MonoBehaviour
    {
        [Tooltip("Reach of the wall along this object's up axis, centred on the object's position")]
        [SerializeField, Min(0f)] private float length = 20f;

        [Tooltip("On: the view stays on this object's positive right side (where the chevrons point). Off: on its negative side")]
        [SerializeField] private bool blockPositive = true;

        [Tooltip("How far in front of the wall (on the kept side) the player must be for the wall to act. Beyond it, or behind the wall, the camera follows freely. The view's half-width is ~9, so anything past ~12 covers every spot where the wall could matter")]
        [SerializeField, Min(0f)] private float zoneDepth = 16f;

        [Tooltip("How far behind the wall line (the hidden side) the zone still reaches. The default 1 keeps a player pressed against the level edge in the zone; raise it if the player can walk past the wall and the view should stay held a while longer")]
        [SerializeField, Min(0f)] private float zoneBehind = 1f;

        public float Length => length;
        public bool BlockPositive => blockPositive;

        /// <summary>Unit normal pointing to the side the view is kept on.</summary>
        public Vector2 Normal => (Vector2)transform.right * (blockPositive ? 1f : -1f);

        /// <summary>Whether a point (the player) is in the wall's zone: within the wall's span,
        /// and between zoneBehind behind the line and zoneDepth out on the kept side.</summary>
        public bool InZone(Vector2 point)
        {
            Vector2 offset = point - (Vector2)transform.position;
            if (Mathf.Abs(Vector2.Dot(offset, transform.up)) > length * 0.5f) return false;
            float front = Vector2.Dot(offset, Normal);
            return front >= -zoneBehind && front <= zoneDepth;
        }

        // Always drawn: a wall you cannot see is a wall you cannot place. The segment itself,
        // chevrons marching toward the kept side, and the zone it acts in as a faint box
        private void OnDrawGizmos()
        {
            Vector2 centre = transform.position;
            Vector2 wallDir = transform.up;
            Vector2 normal = Normal;
            Vector2 half = wallDir * (length * 0.5f);

            Gizmos.color = new Color(1f, 0.45f, 0.2f, 0.9f);
            Gizmos.DrawLine(centre - half, centre + half);

            Gizmos.color = new Color(1f, 0.45f, 0.2f, 0.5f);
            for (int i = 1; i <= 3; i++)
            {
                Vector3 tip = centre + normal * (i * 0.8f);
                Vector3 wingBack = wallDir * 0.4f;
                Gizmos.DrawLine(tip - wingBack, tip);
                Gizmos.DrawLine(tip, tip + wingBack);
            }

            // Zone: from zoneBehind behind the line to zoneDepth out, across the wall's span
            Vector2 back = centre - normal * zoneBehind;
            Vector2 front = centre + normal * zoneDepth;
            Gizmos.color = new Color(1f, 0.45f, 0.2f, 0.2f);
            Gizmos.DrawLine(back - half, front - half);
            Gizmos.DrawLine(back + half, front + half);
            Gizmos.DrawLine(front - half, front + half);
            Gizmos.DrawLine(back - half, back + half);
        }
    }
}

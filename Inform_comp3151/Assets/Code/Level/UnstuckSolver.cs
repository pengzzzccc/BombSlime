using System;
using UnityEngine;

namespace Inkform.Level
{
    /// <summary>
    /// The search behind Unstuck's push-out: the free spot nearest a player wedged into terrain.
    /// Rings of growing radius around the origin, a fixed number of directions per ring; the first
    /// ring with any free spot wins, and within it the most upward one — pushing a wedged player
    /// up and out reads right, pushing them down through a floor does not.
    ///
    /// Pure: what counts as free is the caller's predicate (RespawnDirector checks the player's
    /// capsule against terrain and hazards), so tests drive it with plain shapes.
    /// </summary>
    public static class UnstuckSolver
    {
        public const float DefaultMaxRadius = 3f;
        public const float DefaultStep = 0.25f;
        public const int DirectionsPerRing = 16;

        /// <summary>The free spot nearest origin within maxRadius, or null when there is none —
        /// the caller then falls back to the checkpoint.</summary>
        public static Vector2? FindFreeSpot(Vector2 origin, Func<Vector2, bool> isFree,
            float maxRadius = DefaultMaxRadius, float step = DefaultStep)
        {
            if (isFree == null) return null;
            if (isFree(origin)) return origin;
            if (step <= 0f) return null;

            for (float radius = step; radius <= maxRadius + 1e-4f; radius += step)
            {
                bool found = false;
                Vector2 best = default;
                for (int i = 0; i < DirectionsPerRing; i++)
                {
                    // Start straight up and go round, so the very first direction tried is up
                    float angle = Mathf.PI * 0.5f + i * (Mathf.PI * 2f / DirectionsPerRing);
                    Vector2 candidate = origin + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
                    if (!isFree(candidate)) continue;
                    if (!found || candidate.y > best.y + 1e-4f)
                    {
                        best = candidate;
                        found = true;
                    }
                }
                if (found) return best;
            }
            return null;
        }
    }
}

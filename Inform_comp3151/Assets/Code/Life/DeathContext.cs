using UnityEngine;

namespace Inkform.Life
{
    /// <summary>
    /// All known facts of one death. The strategy decides how this death plays out from them.
    ///
    /// A readonly struct rather than a class: deaths are infrequent, but value-type event parameters
    /// allocate nothing every time — the same lean as Tool/Timer using structs. Always pass with in to
    /// avoid implicit copies of a large struct.
    /// </summary>
    public readonly struct DeathContext
    {
        /// <summary>The deceased. Subscribers claim themselves by it — the publisher (HarmOnTouch) never needs to know the player.</summary>
        public readonly GameObject Victim;

        /// <summary>Kill point. Shards burst outward from here, so it must be the true contact point,
        /// not the publisher's transform.position (on one Tilemap with a whole row of spikes that would
        /// be the grid origin, and shards would fly tens of cells away).</summary>
        public readonly Vector2 From;

        /// <summary>Death cause. DeathDirector picks the strategy by it.</summary>
        public readonly DeathCause Cause;

        /// <summary>The hazard that did it (HarmOnTouch's Interactable root), or null when the
        /// publisher does not say. Presentation never reads it — it exists so analytics (the perf
        /// log's death events) can tell "Gear (12)" from the spike tilemap, which Cause alone cannot.</summary>
        public readonly GameObject Source;

        public DeathContext(GameObject victim, Vector2 from, DeathCause cause, GameObject source = null)
        {
            Victim = victim;
            From = from;
            Cause = cause;
            Source = source;
        }
    }
}

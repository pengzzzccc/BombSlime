using Inkform.Bus;
using Inkform.Fx;
using Inkform.Life;
using Inkform.Tool;
using UnityEngine;

namespace Inkform.Interactable.Parts
{
    /// <summary>
    /// The whole explosive in one part: the blast itself plus the four ways it is set off.
    ///
    /// Detonation paths (deliberately just these — the old speed-threshold part read the
    /// post-solve velocity in the collision callback, where a head-on hit has its normal component
    /// already zeroed, so bombs spat straight into the ground never detonated):
    /// 1. Contact: touching the target (the player) detonates, armed or not;
    /// 2. Hazard contact: touching a hazardDetonatorMask layer (spikes) detonates, armed or not —
    ///    an explosive does not survive resting on spikes;
    /// 3. Contact while armed: a spat/thrown bomb (CarriablePart.Release fires IOnSpit) detonates
    ///    on contact with a worldDetonatorMask layer (Terrain | Breakable by default). Unarmed bombs
    ///    ignore world contact, which is what lets spawner bombs lie on the ground waiting for the
    ///    player; the mask is what keeps an armed bomb from popping on pickups, checkpoints and
    ///    doors — the old maskless version detonated on every Interactable it brushed past;
    /// 4. Chain: caught in another explosion (HazardBus.Exploded, claimed via the victim) it
    ///    detonates after chainDelay, so a row of bombs blows in sequence instead of one frame.
    ///    chainReaction = false makes it immune to this path only.
    ///
    /// Blast propagation runs entirely through HazardBus: RaiseExploded per victim (BreakablePart
    /// shatters, PlayerHandler gets knocked back, other explosives chain) + one RaiseBlast overall
    /// (screen shake / sound / rumble) — all listeners already on the bus, zero changes.
    ///
    /// Proximity warning animation: with triggerFrames + proximityRadius configured, the frame
    /// sequence advances as the player approaches (closer = later frame, 0 = normal), raising Ticked
    /// on change (AudioDirector plays the tick sound).
    /// </summary>
    public class ExplodePart : MonoBehaviour, IInteractablePart, IRestorablePart, IOnSpit
    {
        [Header("Blast")]
        [SerializeField] private float blastRadius = 2f;
        [SerializeField] private float blastForce = 18f;
        [SerializeField] private LayerMask blastMask;
        [Tooltip("What the shatter looks like, all written in this asset; null = silent, no fragments")]
        [SerializeField] private FragmentCue breakCue;

        [Header("Detonation")]
        [Tooltip("Who detonates it by touch, armed or not. CompareTag never errors on a wrong string, it just never matches — use the Tags constants")]
        [SerializeField] private string targetTag = Tags.Player;
        [Tooltip("Once spat out (armed), contact with a worldDetonatorMask layer detonates too." +
            " Off restores pure target-touch behavior")]
        [SerializeField] private bool explodeOnWorldContactWhenArmed = true;
        [Tooltip("Armed world-contact detonates only against these layers — the same Terrain|Breakable" +
            " convention as RopeGun.hitMask / ContactSensor.terrainMask. Without this filter an armed" +
            " bomb pops on pickups, checkpoints, doors, every Interactable it brushes past")]
        [SerializeField] private LayerMask worldDetonatorMask = (1 << 6) | (1 << 11);
        [Tooltip("Contact with these layers detonates regardless of arming — spikes and other" +
            " hazards. Defaults to the Hazard layer; existing prefabs take it via this default")]
        [SerializeField] private LayerMask hazardDetonatorMask = 1 << 13;
        [Tooltip("Caught in another explosion, this one detonates too. Off = immune to chain reactions" +
            " (contact paths still apply)")]
        [SerializeField] private bool chainReaction = true;
        [Tooltip("Delay before detonating after being caught in another explosion (0 = same-frame chain)")]
        [SerializeField] private float chainDelay = 0.1f;

        [Header("Animation")]
        [Tooltip("Proximity warning frames, 0 = normal, later = closer to the player; null/empty = animation off")]
        [SerializeField] private Sprite[] triggerFrames;
        [Tooltip("Frame sequence starts advancing once the player is within this radius; <= 0 disables")]
        [SerializeField] private float proximityRadius = 0f;

        private Interactable root;
        private Collider2D body;
        private SpriteRenderer sprite;
        private bool exploded;      // idempotent: multiple triggers in one frame only detonate once
        private bool armed;         // spat out: world contact detonates from here on
        private bool chainPending;  // caught in a blast, fuse running
        private Timer chainTimer;

        // ---- Proximity warning animation state ----

        private int frameIndex = -1;        // -1 = never set, guaranteeing the first frame is always written
        private Sprite _current;            // SetSprite dedup: per-frame driving but most frames are the same sprite

        // All explosives share one player reference. After the player is destroyed or the scene
        // changes it becomes a Unity fake-null and is re-looked-up on next use, so no ResetStatics
        // needed
        private static Transform playerCache;

        private static Transform Player
        {
            get
            {
                if (playerCache == null)
                {
                    GameObject go = GameObject.FindGameObjectWithTag(Tags.Player);
                    playerCache = go != null ? go.transform : null;
                }
                return playerCache;
            }
        }

        public void Attach(Interactable root)
        {
            this.root = root;
            body = root.GetComponent<Collider2D>();
            sprite = root.GetComponent<SpriteRenderer>();
        }

        void OnEnable() { HazardBus.Exploded += OnChainExploded; }

        void OnDisable() { HazardBus.Exploded -= OnChainExploded; }

        void Update()
        {
            if (chainPending && !chainTimer.IsRunning)
            {
                chainPending = false;
                Explode();
            }
            RefreshFrame();
        }

        // Contact detonation. Enter and Stay both count: a bomb pushed into the player between
        // frames must still go off. The spit-immunity window lives in CarriablePart, which sits
        // earlier in the dispatch order and swallows player contact right after Release
        public bool HandleContact(ContactPhase phase, Collider2D other)
        {
            if (phase == ContactPhase.Exit) return false;
            if (exploded) return false;

            if (other.CompareTag(targetTag))
            {
                Explode();
                return true;    // handled: short-circuit later parts
            }
            if ((hazardDetonatorMask.value & (1 << other.gameObject.layer)) != 0)
            {
                Explode();      // spikes and kin: an explosive does not survive resting on them
                return true;
            }
            if (armed && explodeOnWorldContactWhenArmed
                && (worldDetonatorMask.value & (1 << other.gameObject.layer)) != 0)
            {
                Explode();
                return true;
            }
            return false;
        }

        // IOnSpit: the item left the player's mouth — from now on, world contact detonates
        void IOnSpit.OnSpit() => armed = true;

        // Called by HazardBus when another explosion affects this object: the victim claims itself
        private void OnChainExploded(GameObject victim, Vector2 center, float force)
        {
            if (!chainReaction) return;
            if (root == null || victim != root.gameObject) return;
            chainPending = true;
            chainTimer.Set(chainDelay);
        }

        /// <summary>
        /// Derives the frame to show each frame (proximity warning): closer player = later frame, raising
        /// Ticked on change. No frames while the renderer is disabled (swallowed / hidden / hidden by
        /// the explosion) — a bomb in the mouth must not emit warning ticks.
        /// </summary>
        private void RefreshFrame()
        {
            if (triggerFrames == null || triggerFrames.Length == 0) return;
            if (sprite == null || !sprite.enabled) return;

            int n = triggerFrames.Length;
            Transform p = Player;
            int index;

            if (p == null || proximityRadius <= 0f)
                index = 0;
            else
            {
                float d = Vector2.Distance(transform.position, p.position);
                index = Mathf.Min((int)((1f - Mathf.Clamp01(d / proximityRadius)) * n), n - 1);
            }

            if (index == frameIndex) return;

            // Sound only in the "getting tenser" direction: when the player jitters across a frame
            // boundary, the retreating half stays silent — with SoundCue.cooldown this suffices to
            // suppress jitter; no extra hysteresis needed
            bool advanced = index > frameIndex;
            frameIndex = index;
            SetSprite(triggerFrames[index]);

            if (advanced) HazardBus.RaiseTicked(transform.position, index, n);
        }

        private void SetSprite(Sprite s)
        {
            if (_current == s) return;
            _current = s;
            if (sprite != null) sprite.sprite = s;
        }

        /// <summary>Detonate. Idempotent: takes effect exactly once; callers may invoke freely.</summary>
        public void Explode()
        {
            if (exploded) return;
            exploded = true;

            // Bounds must be captured before disabling the collider: once a Collider2D is disabled its
            // physics shape is removed and bounds degenerate to zero size at the origin
            Bounds bounds = body != null ? body.bounds : new Bounds(root.transform.position, Vector3.one);
            Vector2 center = root.transform.position;

            // Per victim: every object inside the blast circle receives one Exploded
            Collider2D[] hits = Physics2D.OverlapCircleAll(center, blastRadius, blastMask);
            foreach (Collider2D h in hits)
            {
                if (h.gameObject == root.gameObject) continue;      // do not blast the self
                HazardBus.RaiseExploded(h.gameObject, center, blastForce);
            }

            // Overall blast signal: exactly once per explosion; screen shake and such are driven by it
            HazardBus.RaiseBlast(center, blastRadius, blastForce);

            // Restorable items are not destroyed — the part hides them and respawn brings them back.
            // Everything else hides before the shards render (Destroy only applies at frame end, so
            // without hiding the body overlaps the shards for one frame), then is destroyed outright
            bool restorable = root.TryGetPart(out RestorablePart restore);

            if (restorable)
                restore.HideForRestore();
            else
            {
                if (body != null) body.enabled = false;
                foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
            }

            // The shatter look plays for restorable items too: being hidden for restore instead of
            // destroyed is no reason to skip the visual
            Shatter.Burst(breakCue, bounds, center, blastForce);

            // Explosives are unrecoverable: shattered and destroyed outright.
            // The EditMode branch exists so tests can drive Explode() — Destroy is refused outside play mode
            if (!restorable)
            {
                if (Application.isPlaying) Destroy(root.gameObject);
                else DestroyImmediate(root.gameObject);
            }
        }

        // ---- IRestorablePart ----

        /// <summary>
        /// `exploded` is the state that matters here, and leaving it out of the snapshot is what made a
        /// restored bomb inert forever: RestorablePart brought the body back but Explode() still
        /// short-circuited on the first line, and the contact path reported the contact as handled, so
        /// no later part saw it either.
        ///
        /// Captured rather than reset: a bomb that was already spent when the checkpoint was taken
        /// should still be spent after the respawn. The chain fuse and the armed flag ride along for
        /// the same reason — a snapshot reproduces the checkpoint, not "the checkpoint, plus whatever
        /// this part happened to do since".
        /// </summary>
        public IMemento Capture() =>
            new ExplodeMemento(this, exploded, armed, chainPending, chainTimer.Remaining);

        private class ExplodeMemento : IMemento
        {
            private readonly ExplodePart part;
            private readonly bool exploded;
            private readonly bool armed;
            private readonly bool chainPending;
            private readonly float chainRemaining;

            public ExplodeMemento(ExplodePart part, bool exploded, bool armed,
                bool chainPending, float chainRemaining)
            {
                this.part = part;
                this.exploded = exploded;
                this.armed = armed;
                this.chainPending = chainPending;
                this.chainRemaining = chainRemaining;
            }

            public void Restore()
            {
                if (part == null) return;   // part gone (scene change etc.), skip silently

                part.exploded = exploded;
                part.armed = armed;
                part.chainPending = chainPending;
                if (chainRemaining > 0f) part.chainTimer.Set(chainRemaining);
                else part.chainTimer.Clear();

                // Presentation caches, not state — but they still have to be cleared. RefreshFrame only
                // rewrites the sprite when the derived index *changes*, so a bomb that blew up on a late
                // warning frame would come back still drawn mid-warning and stay that way until the
                // player happened to cross a frame boundary. -1 / null guarantee the next RefreshFrame
                // writes whatever frame the current distance calls for.
                part.frameIndex = -1;
                part._current = null;
            }
        }
    }
}

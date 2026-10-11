using Inkform.Audio;
using Inkform.Fx;
using Inkform.Tool;
using System.Collections.Generic;
using UnityEngine;

namespace Inkform.Interactable.Parts
{
    /// <summary>
    /// Timed visibility: cycles hazards between "visible onTime → hidden offTime". While hidden the
    /// collider and renderers are fully off and the player passes through; while visible everything
    /// behaves normally. Objects like a timed laser pillar / timed spike attach this component
    /// directly (a laser pillar only needs a collider and a renderer on top).
    ///
    /// Emitter glow (lasers): the emitter bases are separate sprites placed in the scene, so on
    /// its first frame the hazard finds every sprite using Emitter Sprite that touches its own
    /// shape and gives it an edge glow (SpriteGlow). The glow lights Glow Lead Time before the
    /// hazard appears — the warning — and stays on until it disappears.
    ///
    /// Implementation note: the flip only toggles Collider2D and Renderer enabled, **never SetActive** —
    /// deactivation would stop this component's own Update and it could never wake itself when the
    /// hidden phase ends. This is a project-wide rule (BreakablePart.SetBroken / RestorablePart.SetGone
    /// / CarriablePart.SetVisible all toggle enabled). Works on Tilemaps too: TilemapRenderer is a Renderer.
    /// </summary>
    public class TimedVisibility : MonoBehaviour, IInteractablePart
    {
        [Header("Timing")]
        [Tooltip("Visible (collision/rendering on) duration, seconds")]
        [SerializeField] private float onTime = 2f;
        [Tooltip("Hidden (collision/rendering off) duration, seconds. The player can pass through during this window")]
        [SerializeField] private float offTime = 1.5f;
        [Tooltip("Whether it starts visible or hidden")]
        [SerializeField] private bool startVisible = true;

        [Header("Sound")]
        [Tooltip("Looping world sound gated by this hazard's visibility: audible while visible, cut while hidden. " +
            "Assign an AmbientSource (on this object or a child) with its Cue set, and leave that component enabled " +
            "in the prefab — this gate drives its enabled state. Leave empty for a silent hazard")]
        [SerializeField] private AmbientSource loopSource;

        [Header("Emitter glow")]
        [Tooltip("Sprite of the emitter bases placed in the scene (LaserEmitter_0). Every sprite using it that touches this hazard gets the glow. Empty = no glow")]
        [SerializeField] private Sprite emitterSprite;
        [Tooltip("Material with the Inkform/SpriteOutline shader (LaserGlow); colour and intensity live in the material")]
        [SerializeField] private Material emitterGlowMaterial;
        [Tooltip("Seconds before the hazard appears that the glow lights up; it then stays on until the hazard disappears")]
        [SerializeField, Min(0f)] private float glowLeadTime = 0.2f;
        [Tooltip("How far beyond this hazard's own shape an emitter may sit and still be paired with it, world units")]
        [SerializeField, Min(0f)] private float emitterSearchMargin = 0.3f;
        [Tooltip("Glow drawing space around the emitter sprite, as a fraction of its size")]
        [SerializeField, Min(0f)] private float glowPadding = 0.25f;
        [Tooltip("Glow band thickness, world units")]
        [SerializeField, Min(0f)] private float glowWorldWidth = 0.0625f;

        private Collider2D body;
        private readonly List<Renderer> renderers = new List<Renderer>();
        private Timer timer;
        private bool visible;

        private struct Glow
        {
            public SpriteRenderer emitter;
            public SpriteRenderer renderer;
            public Material material;
        }

        private readonly List<Glow> glows = new List<Glow>();
        private bool glowsResolved;
        private bool glowOn;
        private Bounds hazardBounds;

        // Emitters are claimed by the nearest hazard: a base touching two beams glows for one only.
        // The scene's sprites are scanned once per frame for all hazards resolving in it, not once each
        private static readonly Dictionary<SpriteRenderer, TimedVisibility> emitterOwners =
            new Dictionary<SpriteRenderer, TimedVisibility>();
        private static SpriteRenderer[] sceneSprites;
        private static int sceneSpritesFrame = -1;
        // Glow children reuse the emitter sprite: never mistake one for an emitter base
        private static readonly HashSet<SpriteRenderer> glowRenderers = new HashSet<SpriteRenderer>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            emitterOwners.Clear();
            glowRenderers.Clear();
            sceneSprites = null;
            sceneSpritesFrame = -1;
        }

        public void Attach(Interactable root)
        {
            body = root.GetComponent<Collider2D>();
            if (body == null)
                Debug.LogWarning($"{root.name}'s Interactable has no Collider2D; timed visibility will not work", root);

            // Renderers may sit on the root or children (a Tilemap's rendering is on a child); only
            // toggling enabled, disabled renderers still hand back their references — no activation-order dependency
            renderers.Clear();
            renderers.AddRange(root.GetComponentsInChildren<Renderer>(true));

            visible = startVisible;
            timer.Set(PhaseDuration(visible));
            Apply();
        }

        // Pure driver: consumes no contact, so later parts (like HarmOnTouch) receive it normally
        public bool HandleContact(ContactPhase phase, Collider2D other) => false;

        void Update()
        {
            // First frame: every scene object has run Awake, so the emitter bases all exist
            if (!glowsResolved) ResolveEmitterGlows();

            if (!timer.IsRunning) Flip();
            UpdateGlow();
        }

        private void Flip()
        {
            visible = !visible;
            timer.Set(PhaseDuration(visible));
            Apply();
        }

        // Anti-jitter: a zero duration would flip every frame; clamp to a minimum effective duration
        private float PhaseDuration(bool forOn) => Mathf.Max(forOn ? onTime : offTime, 0.05f);

        private void Apply()
        {
            if (body != null) body.enabled = visible;
            foreach (Renderer r in renderers) r.enabled = visible;

            // The loop follows visibility through AmbientSource's own documented contract: enabling
            // plays its loop, disabling stops it. That keeps AmbientSource the one
            // component that owns looping world audio — no second audio implementation lives here
            if (loopSource != null) loopSource.enabled = visible;
        }

        // ---- Emitter glow ----

        // On through the whole visible phase, and for the last Glow Lead Time of the hidden one —
        // the warning that the hazard is about to appear. Timer runs on Time.time, so a pause holds it
        private void UpdateGlow()
        {
            if (glows.Count == 0) return;
            bool on = visible || timer.Remaining <= glowLeadTime;
            if (on == glowOn) return;
            glowOn = on;
            for (int i = 0; i < glows.Count; i++) ShowGlow(glows[i], on);
        }

        private static void ShowGlow(Glow glow, bool on)
        {
            if (glow.renderer == null) return;
            glow.renderer.enabled = on;
            SpriteGlow.SetFade(glow.material, on ? 1f : 0f);
        }

        private void ResolveEmitterGlows()
        {
            glowsResolved = true;
            if (emitterSprite == null || emitterGlowMaterial == null) return;
            if (!TryGetHazardBounds(out hazardBounds)) return;

            if (sceneSpritesFrame != Time.frameCount || sceneSprites == null)
            {
                sceneSprites = FindObjectsByType<SpriteRenderer>();
                sceneSpritesFrame = Time.frameCount;
            }

            Bounds search = hazardBounds;
            search.Expand(emitterSearchMargin * 2f);
            foreach (SpriteRenderer candidate in sceneSprites)
            {
                if (candidate == null || candidate.sprite != emitterSprite) continue;
                if (glowRenderers.Contains(candidate)) continue;
                Bounds b = candidate.bounds;
                b.center = new Vector3(b.center.x, b.center.y, search.center.z);
                if (!search.Intersects(b)) continue;
                Claim(candidate);
            }
        }

        // This hazard's own shape, from the sprite rather than Renderer/Collider bounds: those go
        // stale or degenerate while the hazard is hidden (disabled), and it may start hidden
        private bool TryGetHazardBounds(out Bounds bounds)
        {
            bounds = default;
            bool found = false;
            foreach (Renderer r in renderers)
            {
                if (!(r is SpriteRenderer sr) || sr.sprite == null) continue;
                Bounds local = sr.sprite.bounds;
                Transform t = sr.transform;
                Vector3 min = local.min, max = local.max;
                Vector3[] corners =
                {
                    t.TransformPoint(new Vector3(min.x, min.y, 0f)),
                    t.TransformPoint(new Vector3(min.x, max.y, 0f)),
                    t.TransformPoint(new Vector3(max.x, min.y, 0f)),
                    t.TransformPoint(new Vector3(max.x, max.y, 0f)),
                };
                foreach (Vector3 c in corners)
                {
                    Vector3 flat = new Vector3(c.x, c.y, 0f);
                    if (!found) { bounds = new Bounds(flat, Vector3.zero); found = true; }
                    else bounds.Encapsulate(flat);
                }
            }
            return found;
        }

        private float DistanceTo(SpriteRenderer emitter)
        {
            Vector3 c = emitter.bounds.center;
            return hazardBounds.SqrDistance(new Vector3(c.x, c.y, 0f));
        }

        private void Claim(SpriteRenderer emitter)
        {
            if (emitterOwners.TryGetValue(emitter, out TimedVisibility owner)
                && owner != null && owner != this)
            {
                if (owner.DistanceTo(emitter) <= DistanceTo(emitter)) return;   // the other hazard is nearer
                owner.DropGlow(emitter);
            }
            emitterOwners[emitter] = this;

            SpriteRenderer glowRenderer = SpriteGlow.Create(emitter, emitterGlowMaterial,
                glowPadding, glowWorldWidth, "Glow", out Material material);
            if (glowRenderer == null) return;
            glowRenderers.Add(glowRenderer);

            var glow = new Glow { emitter = emitter, renderer = glowRenderer, material = material };
            glows.Add(glow);
            ShowGlow(glow, glowOn);
        }

        private void DropGlow(SpriteRenderer emitter)
        {
            for (int i = glows.Count - 1; i >= 0; i--)
            {
                if (glows[i].emitter != emitter) continue;
                ReleaseGlowObject(glows[i]);
                glows.RemoveAt(i);
            }
        }

        private static void ReleaseGlowObject(Glow glow)
        {
            glowRenderers.Remove(glow.renderer);
            if (glow.renderer != null) Destroy(glow.renderer.gameObject);
            SpriteGlow.Release(glow.material);
        }

        // A destroyed hazard must not leave its hum or its glow behind: the loop is gated by this
        // component, and an AmbientSource sitting on a sibling object would otherwise keep playing
        private void OnDestroy()
        {
            if (loopSource != null) loopSource.enabled = false;

            foreach (Glow glow in glows)
            {
                ReleaseGlowObject(glow);
                // By reference: the emitter may already be destroyed in the same scene teardown
                if (!ReferenceEquals(glow.emitter, null)
                    && emitterOwners.TryGetValue(glow.emitter, out TimedVisibility owner)
                    && ReferenceEquals(owner, this))
                    emitterOwners.Remove(glow.emitter);
            }
            glows.Clear();
        }
    }
}

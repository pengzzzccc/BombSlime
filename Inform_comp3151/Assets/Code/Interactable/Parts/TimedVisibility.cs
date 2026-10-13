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
    /// Beam transition (lasers): with a Transition Material (Inkform/LaserBeamTransition) assigned,
    /// the hidden phase opens with the beam dissolving into particles and closes with it
    /// re-forming, both swept from the beam's top end toward its emitter base. Both play inside the
    /// hidden time, so the lethal on-time stays exactly as tuned — and the collider stays off for
    /// the whole hidden phase, transitions included: particles never hurt. Sprite renderers wear the
    /// transition material only while it plays; their own material is handed back the moment the
    /// beam is solid again. Hazards without a SpriteRenderer (spike tilemaps) just flip as before.
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

        public enum BeamBase { Auto, LocalBottom, LocalTop }

        [Header("Beam transition")]
        [Tooltip("Material with the Inkform/LaserBeamTransition shader (LaserBeamTransition). Empty = no transition, the hazard just flips")]
        [SerializeField] private Material transitionMaterial;
        [Tooltip("Seconds the beam spends forming at the end of the hidden phase (no collision while it plays)")]
        [SerializeField, Min(0f)] private float appearTime = 0.175f;
        [Tooltip("Seconds the beam spends breaking up at the start of the hidden phase (no collision while it plays)")]
        [SerializeField, Min(0f)] private float disappearTime = 0.15f;
        [Tooltip("Which end is the base the sweep runs toward. Auto = the end nearest a glowing emitter (local bottom when there is none)")]
        [SerializeField] private BeamBase beamBase = BeamBase.Auto;

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

        // Beam transition state: one material instance per hazard (SpriteRenderer feeds each sprite
        // its own _MainTex through its property block), the materials it replaced, and the beam's
        // measured geometry, resolved once the emitters are known
        private static readonly int FrontId = Shader.PropertyToID("_Front");
        private static readonly int ModeId = Shader.PropertyToID("_Mode");
        private static readonly int FxTimeId = Shader.PropertyToID("_FxTime");
        private Material transitionInstance;
        private readonly List<SpriteRenderer> beamSprites = new List<SpriteRenderer>();
        private readonly List<Material> beamOriginals = new List<Material>();
        private bool transitioning;
        private bool everVisible;
        private float sweepEnd = 1.4f;

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
            everVisible = visible;
            timer.Set(PhaseDuration(visible));
            Apply();
        }

        // Pure driver: consumes no contact, so later parts (like HarmOnTouch) receive it normally
        public bool HandleContact(ContactPhase phase, Collider2D other) => false;

        void Update()
        {
            // First frame: every scene object has run Awake, so the emitter bases all exist
            if (!glowsResolved) { ResolveEmitterGlows(); ResolveBeam(); }

            if (!timer.IsRunning) Flip();
            UpdateTransition();
            UpdateGlow();
        }

        private void Flip()
        {
            visible = !visible;
            if (visible) everVisible = true;
            timer.Set(PhaseDuration(visible));
            Apply();
        }

        // Anti-jitter: a zero duration would flip every frame; clamp to a minimum effective duration
        private float PhaseDuration(bool forOn) => Mathf.Max(forOn ? onTime : offTime, 0.05f);

        private void Apply()
        {
            if (body != null) body.enabled = visible;
            EndTransition();
            foreach (Renderer r in renderers) r.enabled = visible;

            // The loop follows visibility through AmbientSource's own documented contract: enabling
            // plays its loop, disabling stops it. That keeps AmbientSource the one
            // component that owns looping world audio — no second audio implementation lives here
            if (loopSource != null) loopSource.enabled = visible;
        }

        // ---- Beam transition ----

        // Both transitions live inside the hidden phase; if they would not fit, they shrink
        // together so at least a tenth of it stays truly empty
        private void TransitionTimes(out float appear, out float disappear)
        {
            appear = transitionInstance != null ? appearTime : 0f;
            disappear = transitionInstance != null && everVisible ? disappearTime : 0f;
            float budget = PhaseDuration(false) * 0.9f;
            float sum = appear + disappear;
            if (sum > budget && sum > 0f)
            {
                appear *= budget / sum;
                disappear *= budget / sum;
            }
        }

        private void UpdateTransition()
        {
            if (transitionInstance == null || visible) return;

            TransitionTimes(out float appear, out float disappear);
            float remaining = timer.Remaining;
            float elapsed = PhaseDuration(false) - remaining;

            float mode, progress;
            if (disappear > 0f && elapsed < disappear) { mode = 1f; progress = elapsed / disappear; }
            else if (appear > 0f && remaining < appear) { mode = 0f; progress = 1f - remaining / appear; }
            else { EndTransition(); return; }

            BeginTransition();
            // appear eases out (the beam snaps toward the base, then settles); disappear eases in
            float eased = mode < 0.5f ? 1f - (1f - progress) * (1f - progress) : progress * progress;
            transitionInstance.SetFloat(ModeId, mode);
            transitionInstance.SetFloat(FrontId, eased * sweepEnd);
            transitionInstance.SetFloat(FxTimeId, Time.time);
        }

        private void BeginTransition()
        {
            if (transitioning) return;
            transitioning = true;
            beamOriginals.Clear();
            foreach (SpriteRenderer sr in beamSprites)
            {
                beamOriginals.Add(sr.sharedMaterial);
                sr.sharedMaterial = transitionInstance;
                sr.enabled = true;      // rendering only: the collider stays off all hidden phase
            }
        }

        private void EndTransition()
        {
            if (!transitioning) return;
            transitioning = false;
            for (int i = 0; i < beamSprites.Count && i < beamOriginals.Count; i++)
            {
                if (beamSprites[i] == null) continue;
                beamSprites[i].sharedMaterial = beamOriginals[i];
                beamSprites[i].enabled = visible;
            }
            beamOriginals.Clear();
        }

        // Measures the beam once: its long axis and world size (grain stays square on a stretched
        // sprite) and which end is the base — by default the end nearest a glowing emitter
        private void ResolveBeam()
        {
            if (transitionMaterial == null) return;
            beamSprites.Clear();
            foreach (Renderer r in renderers)
                if (r is SpriteRenderer sr && sr.sprite != null && !glowRenderers.Contains(sr)) beamSprites.Add(sr);
            if (beamSprites.Count == 0) return;

            SpriteRenderer main = beamSprites[0];
            Transform t = main.transform;
            Bounds local = main.sprite.bounds;
            Vector3 scale = t.lossyScale;
            float lenX = local.size.x * Mathf.Abs(scale.x), lenY = local.size.y * Mathf.Abs(scale.y);
            bool axisX = lenX > lenY;

            bool baseAtTop = beamBase == BeamBase.LocalTop;
            if (beamBase == BeamBase.Auto && glows.Count > 0)
            {
                Vector3 emitters = Vector3.zero;
                foreach (Glow g in glows) emitters += g.emitter.bounds.center;
                emitters /= glows.Count;
                Vector3 c = local.center;
                Vector3 top = t.TransformPoint(axisX ? new Vector3(local.max.x, c.y, 0f) : new Vector3(c.x, local.max.y, 0f));
                Vector3 bottom = t.TransformPoint(axisX ? new Vector3(local.min.x, c.y, 0f) : new Vector3(c.x, local.min.y, 0f));
                emitters.z = top.z = bottom.z = 0f;
                baseAtTop = (emitters - top).sqrMagnitude < (emitters - bottom).sqrMagnitude;
            }
            // a flipped renderer mirrors the mesh but not the UVs
            if (axisX ? main.flipX : main.flipY) baseAtTop = !baseAtTop;

            transitionInstance = new Material(transitionMaterial) { name = transitionMaterial.name + " (beam)" };
            transitionInstance.SetFloat("_AxisX", axisX ? 1f : 0f);
            transitionInstance.SetFloat("_BaseAtTop", baseAtTop ? 1f : 0f);
            transitionInstance.SetFloat("_Length", Mathf.Max(lenX, lenY));
            transitionInstance.SetFloat("_Width", Mathf.Min(lenX, lenY));
            // the sweep runs past the base by the settle band plus the head, so the beam ends fully
            // solid (appear) or fully gone (disappear) before the material is handed back
            sweepEnd = 1f + transitionInstance.GetFloat("_Settle") + transitionInstance.GetFloat("_HeadWidth");
        }

        // ---- Emitter glow ----

        // On through the whole visible phase, and for the last Glow Lead Time of the hidden one —
        // the warning that the hazard is about to appear. Timer runs on Time.time, so a pause holds it
        private void UpdateGlow()
        {
            if (glows.Count == 0) return;
            // the warning covers the whole re-forming beam, not just the last glowLeadTime
            TransitionTimes(out float appear, out _);
            bool on = visible || timer.Remaining <= Mathf.Max(glowLeadTime, appear);
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

            EndTransition();
            if (transitionInstance != null) Destroy(transitionInstance);

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

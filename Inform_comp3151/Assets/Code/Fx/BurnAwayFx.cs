using Inkform.Bus;
using Inkform.Settings;
using System.Collections.Generic;
using UnityEngine;

namespace Inkform.Fx
{
    /// <summary>
    /// Burn-away death: swaps the body's sprite material for the Inkform/BurnDissolve burn
    /// shader, then marches one number — the burn radius — from the contact point out past the
    /// farthest corner. The flame band and burn-through all live in the shader; this class only
    /// measures the silhouette, places the ignition point, drives the radius — and throws
    /// pixel-square ash off the burning edge while it goes.
    ///
    /// The ash: chunk-sized Point-filtered squares spawn along the visible fire band (rejection
    /// sampled against the same distance field the shader uses), drift upward, settle, fade;
    /// ~40% are ember sparks that blink on/off in fixed ticks instead of glowing smoothly.
    ///
    /// One-shot like BlastWaveFx, except the object lingers until the last ash bit has flown:
    /// Finish() restores the original material and stops emitting immediately (or on Respawned,
    /// so the next life never wakes up wearing burn state), but the bits live out their short
    /// lives first. The body stays visible throughout: the shader dissolves it in place, no
    /// Hide() until the very end.
    /// </summary>
    public class BurnAwayFx : MonoBehaviour
    {
        private struct AshBit
        {
            public Transform transform;
            public SpriteRenderer sprite;
            public Vector2 velocity;
            public float life;
            public float maxLife;
            public float baseAlpha;
            public bool ember;
            public float blinkTimer;
            public bool blinkOn;
        }

        private static Sprite whiteSquare;

        private SpriteRenderer target;
        private Material instance;          // the runtime burn-material copy we hand to the renderer
        private Material originalShared;    // what the renderer wore before the swap
        private float burnDuration;
        private float ashSize = 1f;        // bit size in grain chunks, from the strategy
        private float frontStart;
        private float frontEnd;
        private float time;
        private bool burning = true;

        // ash emission context, captured in Build
        private Bounds bodyBounds;
        private Vector2 originUV;
        private float aspect;
        private float chunkWorld;           // world size of one grain chunk = ash bit size basis
        private float bandWidth;            // the shader's fire band width
        private readonly List<AshBit> bits = new List<AshBit>();
        private float emitAccumulator;

        private const float AshRate = 22f;      // bits per second at FxIntensity 1
        private const int AshCap = 48;          // concurrent bits; extra spawns are skipped
        private const float AshGravity = 2f;    // gentle settle after the upward drift
        private const float AshDrag = 0.6f;
        private const float BlinkTick = 0.08f;  // ember blink period, fixed ticks
        private static readonly Color AshDarkGray = new Color(0.28f, 0.26f, 0.25f, 1f);
        private static readonly Color AshLightGray = new Color(0.62f, 0.58f, 0.55f, 1f);

        /// <summary>
        /// Starts the burn on a sprite. Skips silently when the material is unconfigured — same
        /// contract as Shatter.Burst, the strategy needs no second check. ashSizeMultiplier
        /// scales the ash bits in grain chunks (1 = one shader pixel block).
        /// </summary>
        public static void Spawn(SpriteRenderer target, Material burnMaterial, Vector2 ignitePoint,
                                 float burnDuration, float ashSizeMultiplier = 1f)
        {
            if (target == null || burnMaterial == null) return;

            BurnAwayFx fx = new GameObject("BurnAway").AddComponent<BurnAwayFx>();
            fx.Build(target, burnMaterial, ignitePoint, burnDuration, ashSizeMultiplier);
        }

        private void Build(SpriteRenderer target, Material burnMaterial, Vector2 ignitePoint,
                           float burnDuration, float ashSizeMultiplier)
        {
            this.target = target;
            this.burnDuration = Mathf.Max(0.01f, burnDuration);
            this.ashSize = Mathf.Max(0.1f, ashSizeMultiplier);
            originalShared = target.sharedMaterial;

            // World-space silhouette box, read before anything could disturb it — though nothing
            // does: the body keeps rendering for the whole burn, no Hide() in this death
            bodyBounds = target.bounds;
            aspect = bodyBounds.size.x / Mathf.Max(0.0001f, bodyBounds.size.y);

            instance = new Material(burnMaterial);
            instance.SetFloat("_Aspect", aspect);

            Vector2 uv = WorldToSpriteUV(ignitePoint, bodyBounds, target.flipX, target.flipY);
            originUV = uv;
            instance.SetVector("_BurnOrigin", new Vector4(uv.x * aspect, uv.y, 0f, 0f));

            // Front travel in the shader's aspect space: start below zero — the distance field
            // is never negative, so nothing burns at progress 0; end past the farthest corner
            // plus the noise lift and the border ring, so the ring fully exits the sprite before
            // we restore the material
            frontStart = -0.05f;
            frontEnd = MaxCornerDistance(uv, aspect)
                     + instance.GetFloat("_BurnMult") + instance.GetFloat("_BorderWidth");
            instance.SetFloat("_BurnFront", frontStart);

            // ash context: one grain chunk's world size, and the band the bits spawn along
            Vector4 texel = instance.GetVector("_MainTex_TexelSize");
            chunkWorld = Mathf.Max(0.02f,
                instance.GetFloat("_Chunk") * bodyBounds.size.x / Mathf.Max(1f, texel.z));
            bandWidth = instance.GetFloat("_BorderWidth");

            target.sharedMaterial = instance;   // hand-managed instance; restored in Finish()

            // Safety net: if the respawn beat the burn (short RespawnDelay), bail out cleanly
            // rather than letting the next life spawn mid-dissolve
            LifeBus.Respawned += OnRespawned;
        }

        /// <summary>
        /// World point → sprite UV, honoring flipX/flipY: the sprite renderer mirrors the mesh, so
        /// the world-space left edge wears texture u=1 when the sprite is flipped.
        /// </summary>
        public static Vector2 WorldToSpriteUV(Vector2 world, Bounds bounds, bool flipX, bool flipY)
        {
            float u = Mathf.Approximately(bounds.size.x, 0f)
                ? 0.5f : (world.x - bounds.min.x) / bounds.size.x;
            float v = Mathf.Approximately(bounds.size.y, 0f)
                ? 0.5f : (world.y - bounds.min.y) / bounds.size.y;
            if (flipX) u = 1f - u;
            if (flipY) v = 1f - v;
            return new Vector2(Mathf.Clamp01(u), Mathf.Clamp01(v));
        }

        /// <summary>
        /// Farthest silhouette corner from the ignition point, in aspect space — the distance the
        /// burn front must travel to consume the whole body. Static and pure for tests.
        /// </summary>
        public static float MaxCornerDistance(Vector2 uv, float aspect)
        {
            float d = 0f;
            for (int i = 0; i < 4; i++)
            {
                Vector2 corner = new Vector2((i & 1) * aspect, (i >> 1) & 1);
                d = Mathf.Max(d, Vector2.Distance(corner, new Vector2(uv.x * aspect, uv.y)));
            }
            return d;
        }

        void Update()
        {
            // PresentationDeltaTime: keeps burning (and ash flying) through a death hitStop but
            // freezes under a user pause — the same clock BlastWaveFx runs on
            float dt = GameTimeController.PresentationDeltaTime;
            time += dt;

            if (burning)
            {
                float progress = Mathf.Clamp01(time / burnDuration);
                float front = Mathf.Lerp(frontStart, frontEnd, progress);
                instance.SetFloat("_BurnFront", front);
                instance.SetFloat("_BurnTime", time);   // the shader's flicker clock, frozen with ours under pause

                EmitAsh(front, dt);

                if (progress >= 1f) Finish();
            }

            StepAsh(dt);

            // the object lingers only while ash is still in the air; once the burn is done and
            // the last bit has settled, clean up
            if (!burning && bits.Count == 0)
                Destroy(gameObject);
        }

        /// <summary>
        /// Throws chunk-sized ash squares off the visible fire band: rejection-sample points in
        /// the body's bounds until one lands within the band width of the current radius — the
        /// ash always comes off where the fire actually is.
        /// </summary>
        private void EmitAsh(float front, float dt)
        {
            if (target == null || front < 0f) return;   // before the burn reaches the body

            emitAccumulator += dt * AshRate * SettingsStore.FxIntensity;
            while (emitAccumulator >= 1f)
            {
                emitAccumulator -= 1f;
                if (bits.Count >= AshCap) return;

                for (int attempt = 0; attempt < 8; attempt++)
                {
                    Vector2 world = new Vector2(
                        Random.Range(bodyBounds.min.x, bodyBounds.max.x),
                        Random.Range(bodyBounds.min.y, bodyBounds.max.y));

                    Vector2 uv = WorldToSpriteUV(world, bodyBounds, target.flipX, target.flipY);
                    float dist = Vector2.Distance(
                        new Vector2(uv.x * aspect, uv.y),
                        new Vector2(originUV.x * aspect, originUV.y));

                    if (Mathf.Abs(dist - front) > bandWidth) continue;

                    SpawnBit(world);
                    break;
                }
            }
        }

        private void SpawnBit(Vector2 world)
        {
            GameObject go = new GameObject("AshBit");
            go.transform.SetParent(transform, false);   // dies with us if we die early

            SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = WhiteSquare;
            // The body's own sorting layer, one order above it: the ash flies off in front of the
            // burning player. Copying only the order left the bits on Default, behind the player layer
            if (target != null)
            {
                sr.sortingLayerID = target.sortingLayerID;
                sr.sortingOrder = target.sortingOrder + 1;
            }

            // mostly pale ash flakes; some ember sparks that blink in fixed ticks
            bool ember = Random.value < 0.4f;
            Color color = ember
                ? instance.GetColor("_RampOrange")
                : Color.Lerp(AshDarkGray, AshLightGray, Random.value);
            sr.color = color;

            AshBit bit = new AshBit
            {
                transform = go.transform,
                sprite = sr,
                velocity = new Vector2(Random.Range(-0.6f, 0.6f), Random.Range(0.8f, 1.8f))
                    * SettingsStore.FxIntensity,
                maxLife = Random.Range(0.35f, 0.7f),
                baseAlpha = color.a,
                ember = ember,
                blinkTimer = BlinkTick,
                blinkOn = true,
            };
            bit.life = bit.maxLife;

            bit.transform.position = world;
            bit.transform.localScale = new Vector3(chunkWorld, chunkWorld, 1f)
                * (ashSize * Random.Range(0.6f, 1.4f));

            bits.Add(bit);
        }

        private void StepAsh(float dt)
        {
            for (int i = bits.Count - 1; i >= 0; i--)
            {
                AshBit bit = bits[i];
                bit.life -= dt;

                if (bit.life <= 0f)
                {
                    Destroy(bit.transform.gameObject);
                    bits.RemoveAt(i);
                    continue;
                }

                // drift up, then settle down
                bit.velocity.y -= AshGravity * dt;
                bit.velocity *= 1f - Mathf.Min(1f, AshDrag * dt);
                bit.transform.position += (Vector3)(bit.velocity * dt);

                if (bit.ember)
                {
                    // blink on/off in fixed ticks — pixel sparks never glow smoothly
                    bit.blinkTimer -= dt;
                    if (bit.blinkTimer <= 0f)
                    {
                        bit.blinkOn = !bit.blinkOn;
                        bit.blinkTimer = BlinkTick;
                    }
                    bit.sprite.enabled = bit.blinkOn;
                }
                else if (bit.life < bit.maxLife * 0.4f)
                {
                    // ash flakes fade out over the last 40% of their life
                    float fade = bit.life / (bit.maxLife * 0.4f);
                    bit.sprite.color = new Color(
                        bit.sprite.color.r, bit.sprite.color.g, bit.sprite.color.b,
                        bit.baseAlpha * fade);
                }

                bits[i] = bit;
            }
        }

        private static Sprite WhiteSquare
        {
            get
            {
                if (whiteSquare == null)
                {
                    // a tiny point-filtered white square: tinted gray it is an ash flake,
                    // tinted orange a spark; Point filtering keeps the edges hard
                    var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
                    tex.filterMode = FilterMode.Point;
                    var pixels = new Color32[16];
                    for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(255, 255, 255, 255);
                    tex.SetPixels32(pixels);
                    tex.Apply(false, true);
                    whiteSquare = Sprite.Create(tex, new Rect(0f, 0f, 4f, 4f), new Vector2(0.5f, 0.5f), 4f);
                }
                return whiteSquare;
            }
        }

        private void OnRespawned(GameObject victim, Vector2 pos)
        {
            // Respawn beat the burn: the new life already owns this body — restore the material
            // but keep rendering on, or the player would start their run invisible
            LifeBus.Respawned -= OnRespawned;
            RestoreMaterial();
            StopAndMaybeCleanup();
        }

        private void Finish()
        {
            LifeBus.Respawned -= OnRespawned;

            // Hide before restoring the material: the original material has no dissolve logic and
            // would paint the sprite fully opaque again — "the body pops back after burning away".
            // Disabling rendering (never SetActive) is the same move as IDeathBody.Hide; on
            // respawn PlayerDeathFx.Show() switches rendering back on
            if (target != null) target.enabled = false;
            RestoreMaterial();
            StopAndMaybeCleanup();
        }

        private void StopAndMaybeCleanup()
        {
            burning = false;    // stop emitting; keep stepping until the airborne ash has flown

            if (bits.Count == 0)
                Destroy(gameObject);
        }

        private void RestoreMaterial()
        {
            if (target != null && originalShared != null)
                target.sharedMaterial = originalShared;
            if (instance != null)
                Destroy(instance);      // the runtime copy is ours; never touch the shared asset
        }
    }
}

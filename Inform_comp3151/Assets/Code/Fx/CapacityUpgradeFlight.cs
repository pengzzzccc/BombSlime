using System;
using Inkform.Bus;
using Inkform.Input;
using Inkform.UI;
using UnityEngine;

namespace Inkform.Fx
{
    /// <summary>Tuning for the capacity crystal's flight; lives on InventoryCapacityPart's Inspector.</summary>
    [Serializable]
    public sealed class CapacityFlightSettings
    {
        [Tooltip("Seconds to grow and glide from the pickup to the screen centre (eases out)")]
        [Min(0.01f)] public float riseSeconds = 0.35f;
        [Tooltip("Seconds the crystal hangs at the screen centre, glowing")]
        [Min(0f)] public float holdSeconds = 1f;
        [Tooltip("Seconds of the dash into the HUD backpack (eases in)")]
        [Min(0.01f)] public float flySeconds = 0.3f;
        [Tooltip("The crystal's height at the centre, as a share of the screen height — the same size whatever the room's camera zoom")]
        [Range(0.05f, 0.8f)] public float holdScreenHeight = 0.25f;
        [Tooltip("Its height on arrival, as a share of the HUD backpack plate's height (0.63 = the plate's icon slot)")]
        [Range(0.1f, 1.5f)] public float landPlateShare = 0.63f;
        [Tooltip("While hanging it floats up and back down once, by this share of the screen height")]
        [Range(0f, 0.1f)] public float bobScreenHeight = 0.012f;
        [Tooltip("Edge glow thickness in the crystal's own world units (the glow grows with it)")]
        [Min(0f)] public float glowWorldWidth = 0.1f;
        [Tooltip("Room around the sprite for the glow, as a share of the sprite size — keep it wider than the glow")]
        [Min(0f)] public float glowPadding = 0.3f;
        [Tooltip("Glow breathing speed while hanging (radians per second); 0 = steady")]
        [Min(0f)] public float glowPulseSpeed = 6f;
        [Tooltip("How far the breathing dims the glow at its trough, 0..1")]
        [Range(0f, 1f)] public float glowPulseStrength = 0.3f;
    }

    /// <summary>
    /// The capacity crystal's pickup presentation. Added to the collected crystal by
    /// InventoryCapacityPart; the crystal's own sprite performs it, then is destroyed:
    ///
    ///   Rise  — grows and glides from where it lay to the screen centre while the white edge glow
    ///           (SpriteGlow, the Inkform/SpriteOutline shader) fades in;
    ///   Hold  — hangs there, floating and breathing, for holdSeconds;
    ///   Fly   — dashes into the HUD's backpack plate, shrinking, the glow fading out;
    ///   Land  — gone; the HUD shows the new capacity (it held the old one back until now).
    ///
    /// The world is frozen and gameplay input locked for the whole flight (the wall-map's scoped
    /// freeze: music keeps playing), and it runs on presentation time, so a pause halts it. Each
    /// beat is announced on ItemBus.CapacityFlight for the sounds, the pad and the HUD. Torn down
    /// early (a scene change from the pause menu), it releases everything and announces Cancel.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CapacityUpgradeFlight : MonoBehaviour
    {
        private const int SortingOrder = 500;   // over everything on the topmost sorting layer

        private enum Phase { Rise, Hold, Fly, Done }

        private CapacityFlightSettings settings;
        private SpriteRenderer body;
        private SpriteRenderer glow;
        private Material glowInstance;
        private InputHandler input;
        private GameTimeController gameTime;
        private int increase;

        private Phase phase = Phase.Done;
        private float phaseTime;
        private Vector3 startCentre;    // the sprite's visual centre where it was picked up
        private Vector3 baseScale;
        private float baseHeight;       // the sprite's world height at baseScale
        private Vector3 pivotOffset;    // visual centre minus transform position, at baseScale
        private bool launched;
        private bool finished;

        public void Launch(SpriteRenderer crystal, Material glowTemplate, int capacityIncrease,
            CapacityFlightSettings tuning)
        {
            body = crystal;
            settings = tuning ?? new CapacityFlightSettings();
            increase = capacityIncrease;

            Bounds bounds = body.bounds;
            baseScale = transform.localScale;
            baseHeight = Mathf.Max(0.0001f, bounds.size.y);
            startCentre = bounds.center;
            pivotOffset = bounds.center - transform.position;

            // Over everything the level draws; the glow is built after this so it copies the
            // sorting and lands one above the crystal
            SortingLayer[] layers = SortingLayer.layers;
            if (layers.Length > 0) body.sortingLayerID = layers[layers.Length - 1].id;
            body.sortingOrder = SortingOrder;
            glow = SpriteGlow.Create(body, glowTemplate, settings.glowPadding, settings.glowWorldWidth,
                "Glow", out glowInstance);
            if (glow != null) glow.enabled = true;

            // Same hold as the wall-map inspection: zero the last move sample before input stops
            // forwarding, then lock input and freeze the world
            PlayerBus.Player?.Move(Vector2.zero);
            input = FindAnyObjectByType<InputHandler>();
            if (input != null) input.SetGameplayInputLocked(this, true);
            gameTime = GameTimeController.Instance;
            if (gameTime != null) gameTime.SetWorldFrozen(this, true);

            launched = true;
            phase = Phase.Rise;
            phaseTime = 0f;
            ItemBus.RaiseCapacityFlight(CapacityFlightPhase.Rise, increase);
        }

        void Update()
        {
            if (phase == Phase.Done) return;

            Camera cam = Camera.main;
            if (cam == null)
            {
                Finish(CapacityFlightPhase.Land);
                return;
            }

            phaseTime += GameTimeController.PresentationDeltaTime;

            // Screen anchors in the crystal's plane, re-read every frame so a settling camera
            // never leaves the crystal off-centre
            float depth = startCentre.z - cam.transform.position.z;
            float screenHeight = Mathf.Abs(cam.ViewportToWorldPoint(new Vector3(0.5f, 1f, depth)).y
                                           - cam.ViewportToWorldPoint(new Vector3(0.5f, 0f, depth)).y);
            Vector3 centre = cam.ViewportToWorldPoint(new Vector3(0.5f, 0.5f, depth));
            float holdHeight = screenHeight * settings.holdScreenHeight;

            switch (phase)
            {
                case Phase.Rise:
                {
                    float t = Mathf.Clamp01(phaseTime / settings.riseSeconds);
                    float e = Easing.CubeOut(t);
                    Place(Vector3.LerpUnclamped(startCentre, centre, e), Mathf.Lerp(baseHeight, holdHeight, e));
                    SpriteGlow.SetFade(glowInstance, e);
                    if (t >= 1f) Enter(Phase.Hold, CapacityFlightPhase.Hold);
                    break;
                }
                case Phase.Hold:
                {
                    float t = settings.holdSeconds > 0f ? Mathf.Clamp01(phaseTime / settings.holdSeconds) : 1f;
                    // One slow float up and back, level at both ends so it joins the rise and the dash
                    float bob = Mathf.Sin(t * 2f * Mathf.PI) * settings.bobScreenHeight * screenHeight;
                    Place(centre + Vector3.up * bob, holdHeight);
                    // Breathes down from full (cosine starts at 1), like the interaction prompt's glow
                    float pulse = 1f - settings.glowPulseStrength * 0.5f
                        * (1f - Mathf.Cos(phaseTime * settings.glowPulseSpeed));
                    SpriteGlow.SetFade(glowInstance, pulse);
                    if (t >= 1f) Enter(Phase.Fly, CapacityFlightPhase.Fly);
                    break;
                }
                case Phase.Fly:
                {
                    float t = Mathf.Clamp01(phaseTime / settings.flySeconds);
                    float e = Easing.CubeIn(t);
                    HudTarget(cam, depth, screenHeight, out Vector3 target, out float landHeight);
                    Place(Vector3.LerpUnclamped(centre, target, e), Mathf.Lerp(holdHeight, landHeight, e));
                    SpriteGlow.SetFade(glowInstance, 1f - t);
                    if (t >= 1f) Finish(CapacityFlightPhase.Land);
                    break;
                }
            }
        }

        private void Enter(Phase next, CapacityFlightPhase beat)
        {
            phase = next;
            phaseTime = 0f;
            ItemBus.RaiseCapacityFlight(beat, increase);
        }

        // Puts the sprite's visual centre at `centre` with the given world height, scaling about
        // the transform (the pivot offset scales with it)
        private void Place(Vector3 centre, float height)
        {
            float k = height / baseHeight;
            transform.localScale = baseScale * k;
            transform.position = centre - pivotOffset * k;
        }

        // The HUD backpack plate in the crystal's plane: its centre, and the arrival height. Falls
        // back to the bottom-right corner when the HUD cannot say where the plate is.
        private void HudTarget(Camera cam, float depth, float screenHeight, out Vector3 target, out float height)
        {
            if (UIManager.Instance != null && Screen.height > 0
                && UIManager.Instance.TryGetHudInventoryRect(out Rect plate))
            {
                target = cam.ScreenToWorldPoint(new Vector3(plate.center.x, plate.center.y, depth));
                height = screenHeight * plate.height / Screen.height * settings.landPlateShare;
                return;
            }

            target = cam.ViewportToWorldPoint(new Vector3(0.93f, 0.08f, depth));
            height = screenHeight * 0.05f;
        }

        private void Finish(CapacityFlightPhase beat)
        {
            if (finished) return;
            finished = true;
            phase = Phase.Done;

            if (body != null) body.enabled = false;
            if (glow != null) glow.enabled = false;
            ItemBus.RaiseCapacityFlight(beat, increase);
            ReleaseHold();
            if (beat == CapacityFlightPhase.Land) Destroy(gameObject);
        }

        private void ReleaseHold()
        {
            if (gameTime != null) gameTime.SetWorldFrozen(this, false);
            gameTime = null;
            if (input != null) input.SetGameplayInputLocked(this, false);
            input = null;
        }

        void OnDestroy()
        {
            // Torn down before it arrived (a scene change): the HUD must stop holding the capacity
            // back, and time and controls must not stay with a destroyed owner
            if (launched) Finish(CapacityFlightPhase.Cancel);
            ReleaseHold();
            SpriteGlow.Release(glowInstance);
        }
    }
}

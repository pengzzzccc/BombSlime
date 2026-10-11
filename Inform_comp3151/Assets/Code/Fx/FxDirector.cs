using Inkform.Bus;
using Inkform.Settings;
using UnityEngine;

namespace Inkform.Fx
{
    /// <summary>
    /// FX director: translates "what happened in the game" into "what feedback the screen should give".
    /// Subscribes HazardBus.Blast (exactly once per explosion), attenuates by distance from the blast
    /// center to the camera, then raises the matching FxBus commands.
    /// Tuning for "how much an explosion shakes" is centralized in this one Inspector —
    /// explosives never need to know FX exist, and CamHandler / ScreenFx never need to know explosions exist.
    /// The post vignette is death-exclusive, so no RaisePunch here — explosions only shake / hitstop / zoom.
    ///
    /// Death feedback is not here either: it dispatches by cause rather than by concern, the whole
    /// presentation belongs to the DeathStrategy asset (see Life/ShatterDeathStrategy). This class is
    /// left with only the explosion branch.
    /// Attach to Main Camera (distance attenuation uses this object's position as the viewpoint).
    /// </summary>
    public class FxDirector : MonoBehaviour
    {
        [Header("Blast falloff")]
        [SerializeField] private float falloffRange = 20f;      // blasts farther than this are completely unfelt

        [Header("Blast FX")]
        [SerializeField] private float blastTrauma = 0.55f;
        [SerializeField] private float blastHitStop = 0.06f;
        [SerializeField] private float blastZoom = -0.35f;      // negative = push in
        [SerializeField] private float blastZoomTime = 0.25f;

        [Header("Blast Wave")]
        [SerializeField] private bool blastWaveEnabled = true;
        [SerializeField] private Color blastWaveColor = new Color(1f, 0.81960785f, 0.36078432f, 0.95f);
        [SerializeField, Min(0f)] private float blastWaveDuration = 0.28f;
        [SerializeField, Min(0f)] private float blastWaveStartRadius = 0.12f;
        [SerializeField, Min(0f)] private float blastWaveStartWidth = 0.22f;
        [SerializeField, Min(0f)] private float blastWaveEndWidth = 0.04f;
        [SerializeField, Range(24, 128)] private int blastWaveSegments = 64;
        [Tooltip("Sorting layer name of the ring; drawn with the player, above it by the order below")]
        [SerializeField] private string blastWaveSortingLayer = "player";
        [SerializeField] private int blastWaveSortingOrder = 2;

        void OnEnable()
        {
            HazardBus.Blast += OnBlast;
        }

        void OnDisable()
        {
            HazardBus.Blast -= OnBlast;
        }

        private void OnBlast(Vector2 center, float radius, float force)
        {
            float k = falloffRange <= 0f
                ? 1f
                : Mathf.Clamp01(1f - Vector2.Distance(center, transform.position) / falloffRange);
            if (k <= 0f) return;

            float waveOpacity = Mathf.Clamp01(SettingsStore.FxIntensity) * k;
            if (blastWaveEnabled && waveOpacity > 0f)
            {
                BlastWaveFx.Spawn(
                    center,
                    radius,
                    blastWaveColor,
                    blastWaveDuration,
                    blastWaveStartRadius,
                    blastWaveStartWidth,
                    blastWaveEndWidth,
                    blastWaveSegments,
                    SortingLayer.NameToID(blastWaveSortingLayer),
                    blastWaveSortingOrder,
                    waveOpacity);
            }

            // Each item can be zeroed out separately in the Inspector without affecting the others
            if (blastTrauma > 0f) FxBus.RaiseShake(blastTrauma * k);
            if (blastHitStop > 0f) FxBus.RaiseHitStop(blastHitStop * k);
            if (blastZoom != 0f) FxBus.RaiseZoom(blastZoom * k, blastZoomTime);
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.magenta;
            Gizmos.DrawWireSphere(transform.position, falloffRange);
        }
    }
}

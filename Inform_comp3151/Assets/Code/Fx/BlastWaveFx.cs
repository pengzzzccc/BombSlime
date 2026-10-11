using UnityEngine;
using UnityEngine.Rendering;

namespace Inkform.Fx
{
    /// <summary>
    /// One-shot world-space explosion ring. It is presentation only: no collider, rigidbody or bus
    /// publication. FxDirector creates one from each overall HazardBus.Blast signal.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BlastWaveFx : MonoBehaviour
    {
        private static Material sharedWaveMaterial;

        private LineRenderer line;
        private Color baseColor;
        private float duration;
        private float startRadius;
        private float targetRadius;
        private float startWidth;
        private float endWidth;
        private float opacityMultiplier;
        private float elapsed;
        private float currentRadius;

        public float CurrentRadius => currentRadius;

        /// <summary>Creates and immediately draws one blast wave. Invalid or invisible settings return null.</summary>
        public static BlastWaveFx Spawn(
            Vector2 center,
            float targetRadius,
            Color color,
            float duration,
            float startRadius,
            float startWidth,
            float endWidth,
            int segments,
            int sortingLayerID,
            int sortingOrder,
            float opacityMultiplier)
        {
            opacityMultiplier = Mathf.Clamp01(opacityMultiplier);
            if (targetRadius <= 0f || duration <= 0f || opacityMultiplier <= 0f) return null;

            Material material = SharedWaveMaterial;
            if (material == null) return null;

            var go = new GameObject("Blast Wave FX");
            go.transform.position = new Vector3(center.x, center.y, 0f);

            LineRenderer line = go.AddComponent<LineRenderer>();
            line.sharedMaterial = material;
            line.useWorldSpace = false;
            line.loop = true;
            line.alignment = LineAlignment.View;
            line.textureMode = LineTextureMode.Stretch;
            line.numCornerVertices = 2;
            line.numCapVertices = 0;
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.lightProbeUsage = LightProbeUsage.Off;
            line.reflectionProbeUsage = ReflectionProbeUsage.Off;
            line.sortingLayerID = sortingLayerID;
            line.sortingOrder = sortingOrder;

            BlastWaveFx wave = go.AddComponent<BlastWaveFx>();
            wave.Initialize(
                line,
                targetRadius,
                color,
                duration,
                startRadius,
                startWidth,
                endWidth,
                segments,
                opacityMultiplier);
            return wave;
        }

        private static Material SharedWaveMaterial
        {
            get
            {
                if (sharedWaveMaterial != null) return sharedWaveMaterial;

                Shader shader = Shader.Find("Sprites/Default");
                if (shader == null)
                {
                    Debug.LogWarning("BlastWaveFx: Sprites/Default shader is unavailable; skipping the blast wave");
                    return null;
                }

                sharedWaveMaterial = new Material(shader)
                {
                    name = "Blast Wave Shared Material",
                    hideFlags = HideFlags.HideAndDontSave
                };
                return sharedWaveMaterial;
            }
        }

        private void Initialize(
            LineRenderer line,
            float targetRadius,
            Color color,
            float duration,
            float startRadius,
            float startWidth,
            float endWidth,
            int segments,
            float opacityMultiplier)
        {
            this.line = line;
            this.targetRadius = Mathf.Max(0f, targetRadius);
            this.baseColor = color;
            this.duration = Mathf.Max(0.0001f, duration);
            this.startRadius = Mathf.Clamp(startRadius, 0f, this.targetRadius);
            this.startWidth = Mathf.Max(0f, startWidth);
            this.endWidth = Mathf.Max(0f, endWidth);
            this.opacityMultiplier = Mathf.Clamp01(opacityMultiplier);

            int pointCount = Mathf.Clamp(segments, 24, 128);
            line.positionCount = pointCount;
            Apply(0f);
        }

        private void Update() => Advance(GameTimeController.PresentationDeltaTime);

        /// <summary>Deterministic animation step, kept separate from Update for EditMode coverage.</summary>
        private void Advance(float deltaTime)
        {
            if (deltaTime <= 0f) return;

            elapsed = Mathf.Min(duration, elapsed + deltaTime);
            float normalized = elapsed / duration;
            Apply(normalized);

            if (elapsed < duration) return;
#if UNITY_EDITOR
            if (!Application.isPlaying)
                DestroyImmediate(gameObject);
            else
#endif
                Destroy(gameObject);
        }

        private void Apply(float normalized)
        {
            normalized = Mathf.Clamp01(normalized);
            float remaining = 1f - normalized;
            float eased = 1f - remaining * remaining * remaining;
            currentRadius = Mathf.Lerp(startRadius, targetRadius, eased);

            int pointCount = line.positionCount;
            for (int i = 0; i < pointCount; i++)
            {
                float angle = Mathf.PI * 2f * i / pointCount;
                line.SetPosition(i, new Vector3(Mathf.Cos(angle) * currentRadius, Mathf.Sin(angle) * currentRadius, 0f));
            }

            line.widthMultiplier = Mathf.Lerp(startWidth, endWidth, normalized);
            Color color = baseColor;
            color.a *= opacityMultiplier * remaining;
            line.startColor = color;
            line.endColor = color;
        }
    }
}

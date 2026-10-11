using UnityEngine;

namespace Inkform.Fx
{
    /// <summary>
    /// Edge glow around a sprite, drawn by the Inkform/SpriteOutline shader. A child SpriteRenderer
    /// carries the same sprite on a private copy of the glow material: its quad is scaled up by the
    /// padding (a sprite mesh has no pixels outside its silhouette) and the shader remaps the UVs
    /// back into sprite space, so the silhouette keeps its size and the ring around it becomes the
    /// glowing band. Brightness is the material's _Fade (0..1).
    ///
    /// Shared by the interaction prompt outline and the laser emitter glow.
    /// </summary>
    public static class SpriteGlow
    {
        private static readonly int FadeId = Shader.PropertyToID("_Fade");
        private static readonly int OutlineWidthId = Shader.PropertyToID("_OutlineWidth");
        private static readonly int SpriteRectId = Shader.PropertyToID("_SpriteRect");
        private static readonly int SpriteScaleId = Shader.PropertyToID("_SpriteScale");
        private static readonly int SpriteUvStepId = Shader.PropertyToID("_SpriteUvStep");

        /// <summary>
        /// Builds the glow under <paramref name="source"/>, disabled and at fade 0, sorted just above
        /// it. worldWidth is the band thickness in world units, converted to texels so sprites with
        /// different Pixels Per Unit glow equally thick. Returns null (and no material) when the
        /// source has no usable sprite or no template material.
        /// </summary>
        public static SpriteRenderer Create(SpriteRenderer source, Material template, float padding,
            float worldWidth, string childName, out Material instance)
        {
            instance = null;
            Sprite sprite = source != null ? source.sprite : null;
            if (template == null || !TrySpriteUvRect(sprite, out Vector4 spriteRect)) return null;

            var glowObject = new GameObject(childName);
            glowObject.layer = source.gameObject.layer;
            // Parent to the renderer itself so all of its position, rotation and scale are
            // inherited exactly once
            glowObject.transform.SetParent(source.transform, false);

            float scale = 1f + 2f * Mathf.Max(0f, padding);
            glowObject.transform.localScale = Vector3.one * scale;

            // Sprite.bounds is available synchronously, unlike a newly-created renderer's
            // localBounds. Account for SpriteRenderer flip when keeping an off-centre pivot fixed.
            Vector2 spriteCenter = sprite.bounds.center;
            if (source.flipX) spriteCenter.x = -spriteCenter.x;
            if (source.flipY) spriteCenter.y = -spriteCenter.y;
            glowObject.transform.localPosition = spriteCenter - scale * spriteCenter;

            // Configure the private material completely before the renderer is enabled, so the
            // first 2D-renderer draw already has valid UV, texel and fade data
            Texture2D texture = sprite.texture;
            instance = new Material(template) { name = $"{template.name} ({source.name})" };
            instance.SetVector(SpriteRectId, spriteRect);
            instance.SetFloat(SpriteScaleId, scale);
            instance.SetVector(SpriteUvStepId, new Vector4(
                1f / texture.width, 1f / texture.height, texture.width, texture.height));
            instance.SetFloat(OutlineWidthId, worldWidth * sprite.pixelsPerUnit);
            instance.SetFloat(FadeId, 0f);

            SpriteRenderer glow = glowObject.AddComponent<SpriteRenderer>();
            glow.enabled = false;
            glow.sprite = sprite;
            glow.sharedMaterial = instance;
            glow.sortingLayerID = source.sortingLayerID;
            glow.sortingOrder = source.sortingOrder + 1;
            glow.flipX = source.flipX;
            glow.flipY = source.flipY;
            glow.drawMode = source.drawMode;
            glow.size = source.size;
            glow.tileMode = source.tileMode;
            glow.maskInteraction = source.maskInteraction;
            glow.spriteSortPoint = source.spriteSortPoint;
            return glow;
        }

        public static void SetFade(Material instance, float fade)
        {
            if (instance != null) instance.SetFloat(FadeId, fade);
        }

        /// <summary>The per-renderer material copy is not released with its GameObject — drop it
        /// explicitly or it lingers until UnloadUnusedAssets (DestroyImmediate in edit-mode tests).</summary>
        public static void Release(Material instance)
        {
            if (instance == null) return;
#if UNITY_EDITOR
            if (!Application.isPlaying) Object.DestroyImmediate(instance);
            else
#endif
                Object.Destroy(instance);
        }

        /// <summary>The sprite's slice rectangle in texture UV space (x0, y0, x1, y1) — the region the
        /// outline shader treats as "the sprite", masking everything outside it.</summary>
        private static bool TrySpriteUvRect(Sprite sprite, out Vector4 uvRect)
        {
            uvRect = default;
            if (sprite == null) return false;

            Texture2D texture = sprite.texture;
            if (texture == null || texture.width <= 0 || texture.height <= 0) return false;

            Rect pixelRect = sprite.textureRect;
            uvRect = new Vector4(
                pixelRect.x / texture.width,
                pixelRect.y / texture.height,
                (pixelRect.x + pixelRect.width) / texture.width,
                (pixelRect.y + pixelRect.height) / texture.height);
            return true;
        }
    }
}

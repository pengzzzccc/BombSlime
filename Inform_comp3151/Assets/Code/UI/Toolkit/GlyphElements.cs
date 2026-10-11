using UnityEngine;
using UnityEngine.UIElements;

namespace Inkform.UI
{
    /// <summary>Glyph sizes: Normal for the rebind table, Small for the menu prompt bar, Large for
    /// the tutorial hint line.</summary>
    public enum GlyphSize { Normal, Small, Large }

    /// <summary>
    /// Turns an InputGlyphs.Glyph into its UI Toolkit element, so every place that shows a
    /// button — the tutorial hint, the menu prompt bar, the rebind table — draws it the same way:
    /// pad art at the sprite's own aspect, or a keycap (.key-cap in Theme.uss) carrying the
    /// key's name. Nothing it builds takes pointer input.
    /// </summary>
    public static class GlyphElements
    {
        public const float Height = 40f;        // matches --key-h
        public const float SmallHeight = 32f;   // matches .key--small
        public const float LargeHeight = 60f;   // matches .key--large

        public static VisualElement Create(InputGlyphs.Glyph glyph, GlyphSize size = GlyphSize.Normal)
        {
            if (glyph.sprite != null)
            {
                var icon = new VisualElement { pickingMode = PickingMode.Ignore };
                icon.AddToClassList("key-icon");
                icon.style.backgroundImage = new StyleBackground(glyph.sprite);
                // Keep the sprite's aspect: shoulder and stick icons are wider than they are tall
                float height = HeightOf(size);
                Rect r = glyph.sprite.rect;
                icon.style.height = height;
                icon.style.width = r.height > 0f ? height * r.width / r.height : height;
                return icon;
            }

            var key = new VisualElement { pickingMode = PickingMode.Ignore };
            key.AddToClassList("key-cap");
            if (size == GlyphSize.Small) key.AddToClassList("key--small");
            else if (size == GlyphSize.Large) key.AddToClassList("key--large");
            var name = new Label(glyph.label) { pickingMode = PickingMode.Ignore };
            name.AddToClassList("key-cap-label");
            key.Add(name);
            return key;
        }

        private static float HeightOf(GlyphSize size) =>
            size == GlyphSize.Small ? SmallHeight : size == GlyphSize.Large ? LargeHeight : Height;
    }
}

using UnityEngine;

namespace Inkform.EditorTools
{
    /// <summary>
    /// Colors and marker shapes shared by the Scene-view overlay — the same tokens as the web viewer
    /// (Docs/Tool/PerfLogViewer/css/viewer.css + js/theme.js, dark set), so a death that is a blue
    /// triangle in the browser is a blue triangle on the level art too. Change both together.
    /// </summary>
    public static class PerfLogStyle
    {
        public enum Shape { Triangle, Diamond, Square, Star, Circle, Ring }

        // Categorical slots, fixed order (validated palette, dark-surface steps)
        public static readonly Color[] Series =
        {
            Hex(0x3987e5), Hex(0xd95926), Hex(0x199e70), Hex(0xc98500),
            Hex(0xd55181), Hex(0x008300), Hex(0x9085e9), Hex(0xe66767),
        };
        public static readonly Color Other = Hex(0x6b6a64);

        // Status — difficulty ratings only, always shown with a label
        public static readonly Color Good = Hex(0x0ca30c);
        public static readonly Color Warning = Hex(0xfab219);
        public static readonly Color Serious = Hex(0xec835a);
        public static readonly Color Critical = Hex(0xd03b3b);

        // Death heat: single-hue ramp, faint → mid → peak
        public static readonly Color[] DeathRamp = { Hex(0x5a1414), Hex(0xe66767), Hex(0xffd9d2) };

        public static readonly Color Ink = Hex(0xffffff);
        public static readonly Color InkMuted = Hex(0xc3c2b7);
        public static readonly Color Surface = Hex(0x1a1a19);

        public static Color SessionColor(int slot) => slot < Series.Length ? Series[slot] : Other;

        public static Color CauseColor(string cause)
        {
            switch (cause)
            {
                case "Spike": return Series[0];
                case "Burned": return Series[1];
                case "Crushed": return Series[2];
                case "Blast": return Series[3];
                case "Void": return Series[4];
                default: return Other;
            }
        }

        public static Shape CauseShape(string cause, bool inferred)
        {
            if (inferred) return Shape.Ring;
            switch (cause)
            {
                case "Spike": return Shape.Triangle;
                case "Burned": return Shape.Diamond;
                case "Crushed": return Shape.Square;
                case "Blast": return Shape.Star;
                case "Void": return Shape.Circle;
                default: return Shape.Ring;
            }
        }

        /// <summary>Mean deaths per pass → rating color + label key (same thresholds as the viewer).</summary>
        public static Color Rating(float meanDeaths, out string label)
        {
            if (meanDeaths < 0.5f) { label = "Easy"; return Good; }
            if (meanDeaths < 1.5f) { label = "Moderate"; return Warning; }
            if (meanDeaths < 3f) { label = "Hard"; return Serious; }
            label = "Brutal";
            return Critical;
        }

        public static Color Ramp(Color[] stops, float t)
        {
            t = Mathf.Clamp01(t);
            Color c = t < 0.5f ? Color.Lerp(stops[0], stops[1], t / 0.5f) : Color.Lerp(stops[1], stops[2], (t - 0.5f) / 0.5f);
            c.a = Mathf.Min(1f, Mathf.Pow(t, 0.55f) * 1.08f) * 0.95f;
            return c;
        }

        public static Color Hex(int rgb, float a = 1f) =>
            new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, a);
    }
}

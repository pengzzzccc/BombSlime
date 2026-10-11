using UnityEngine;

namespace Inkform.UI
{
    /// <summary>
    /// The one place the game's UI font is chosen: the UI Toolkit root (UIManager) and the
    /// world-space interaction prompt (InteractionPromptPart) both read Primary, so swapping the
    /// font is a one-line change to Candidates.
    ///
    /// Alibaba PuHuiTi carries full Latin plus ~21k CJK glyphs; its font data ships with the build
    /// (includeFontData), so glyphs are rasterized on demand on every target including WebGL.
    /// </summary>
    public static class UiFonts
    {
        // First that loads wins; LegacyRuntime (Arial metrics) is the final fallback
        private static readonly string[] Candidates = { "UI/AlibabaPuHuiTi", "UI/LiberationSans", "UI/BombSlimeFonts" };

        private static Font primary;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => primary = null;

        public static Font Primary => primary != null ? primary : primary = Load();

        private static Font Load()
        {
            foreach (string candidate in Candidates)
            {
                Font loaded = Resources.Load<Font>(candidate);
                if (loaded != null) return loaded;
            }
            return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        }
    }
}

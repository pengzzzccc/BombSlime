using UnityEngine;

namespace Inkform.UI
{
    /// <summary>
    /// Layout rules of the HUD's bomb slot (Art/UI/bombslot.png): which plate colour the fill
    /// picks, and how big the carried bombs are on the plate's line. Pure (no Toolkit), so tests
    /// can pin it; Hud builds the elements from it.
    ///
    /// Art units: every plate is 61x19 with its line at x 23..51, y 13; the small bomb sprites
    /// are 9x9 with a 1 px transparent border, so two sprites side by side already leave a 2 px
    /// gap between the visible bombs.
    /// </summary>
    public static class BombSlot
    {
        public const int PlateWidth = 61;
        public const int PlateHeight = 19;
        public const int LineLeft = 23;
        public const int LineWidth = 29;
        public const int LineY = 13;
        public const int BombSprite = 9;

        /// <summary>Plate colours, emptiest to fullest — the redder, the fuller.</summary>
        public enum Tier { White, Green, Yellow, Orange, Red }

        /// <summary>Empty is white, full is red; in between by thirds of the capacity: up to a
        /// third green, up to two thirds yellow, short of full orange.</summary>
        public static Tier TierFor(int count, int capacity)
        {
            if (count <= 0) return Tier.White;
            if (count >= capacity) return Tier.Red;
            if (3 * count <= capacity) return Tier.Green;
            if (3 * count <= 2 * capacity) return Tier.Yellow;
            return Tier.Orange;
        }

        /// <summary>Side of each bomb sprite (square) for count bombs sharing a row of the given
        /// width: their own size while they fit side by side, shrunk evenly once they would not —
        /// never bigger than maxSize, never overlapping. Bomb i sits at i * size.</summary>
        public static float BombSize(int count, float available, float maxSize)
        {
            if (count <= 0 || available <= 0f) return 0f;
            return Mathf.Min(maxSize, available / count);
        }
    }
}

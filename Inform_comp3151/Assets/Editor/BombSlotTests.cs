#if UNITY_INCLUDE_TESTS
using Inkform.UI;
using NUnit.Framework;

namespace Inkform.Tests
{
    /// <summary>
    /// The HUD bomb slot's layout rules (BombSlot): the plate colour by fill, and how big the
    /// carried bombs are so they share the line without overlapping.
    /// </summary>
    public sealed class BombSlotTests
    {
        [Test]
        public void Tier_EmptyIsWhite_FullIsRed()
        {
            Assert.AreEqual(BombSlot.Tier.White, BombSlot.TierFor(0, 0));
            Assert.AreEqual(BombSlot.Tier.White, BombSlot.TierFor(0, 5));
            Assert.AreEqual(BombSlot.Tier.Red, BombSlot.TierFor(1, 1));
            Assert.AreEqual(BombSlot.Tier.Red, BombSlot.TierFor(5, 5));
        }

        [Test]
        public void Tier_RisesByThirdsTowardRed()
        {
            // Capacity 5: 1 green, 2-3 yellow, 4 orange, 5 red
            Assert.AreEqual(BombSlot.Tier.Green, BombSlot.TierFor(1, 5));
            Assert.AreEqual(BombSlot.Tier.Yellow, BombSlot.TierFor(2, 5));
            Assert.AreEqual(BombSlot.Tier.Yellow, BombSlot.TierFor(3, 5));
            Assert.AreEqual(BombSlot.Tier.Orange, BombSlot.TierFor(4, 5));

            // Exact thirds land on the lower tier
            Assert.AreEqual(BombSlot.Tier.Green, BombSlot.TierFor(1, 3));
            Assert.AreEqual(BombSlot.Tier.Yellow, BombSlot.TierFor(2, 3));
            Assert.AreEqual(BombSlot.Tier.Yellow, BombSlot.TierFor(1, 2));
        }

        [Test]
        public void BombSize_NeverExceedsTheSpriteItself()
        {
            // Plenty of room: a lone bomb and a pair keep their own size
            Assert.AreEqual(67.5f, BombSlot.BombSize(1, 160f, 67.5f));
            Assert.AreEqual(67.5f, BombSlot.BombSize(2, 160f, 67.5f));
        }

        [Test]
        public void BombSize_ShrinksToFitWithoutOverlap()
        {
            foreach (int n in new[] { 3, 5, 8, 20 })
            {
                float size = BombSlot.BombSize(n, 160f, 67.5f);
                Assert.Less(size, 67.5f, $"{n} bombs: shrunk");
                Assert.LessOrEqual(n * size, 160f + 0.001f, $"{n} bombs: the row holds them all, side by side");
                Assert.AreEqual(160f / n, size, 0.001f, $"{n} bombs: shrunk no more than needed");
            }
        }

        [Test]
        public void BombSize_ZeroWhenEmptyOrNoRoom()
        {
            Assert.AreEqual(0f, BombSlot.BombSize(0, 160f, 67.5f));
            Assert.AreEqual(0f, BombSlot.BombSize(3, 0f, 67.5f));
        }
    }
}
#endif

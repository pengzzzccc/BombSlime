#if UNITY_INCLUDE_TESTS
using Inkform.Level;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Inkform.Tests
{
    /// <summary>
    /// Unstuck's push-out search (UnstuckSolver), driven with plain shapes standing in for the
    /// player's capsule against terrain, plus the pause menu's button contract.
    /// </summary>
    public sealed class UnstuckSolverTests
    {
        [Test]
        public void AlreadyFree_StaysPut()
        {
            Vector2? spot = UnstuckSolver.FindFreeSpot(new Vector2(3f, 4f), _ => true);
            Assert.AreEqual(new Vector2(3f, 4f), spot);
        }

        [Test]
        public void WedgedInABlock_ComesOutAtTheNearestEdge()
        {
            // Solid for x < 1 (a wall on the left): the nearest free ground is just past x = 1
            Vector2? spot = UnstuckSolver.FindFreeSpot(Vector2.zero, p => p.x >= 1f);
            Assert.IsTrue(spot.HasValue);
            Assert.GreaterOrEqual(spot.Value.x, 1f);
            Assert.LessOrEqual(spot.Value.magnitude, 1f + UnstuckSolver.DefaultStep + 1e-3f, "the first ring that clears the wall");
        }

        [Test]
        public void EquallyNear_PrefersUp()
        {
            // A horizontal slab |y| < 0.6: up and down are both free at the same distance
            Vector2? spot = UnstuckSolver.FindFreeSpot(Vector2.zero, p => Mathf.Abs(p.y) >= 0.6f);
            Assert.IsTrue(spot.HasValue);
            Assert.Greater(spot.Value.y, 0f, "pushed up out of the slab, not down through it");
        }

        [Test]
        public void NothingFreeInRange_ReturnsNull()
        {
            Assert.IsNull(UnstuckSolver.FindFreeSpot(Vector2.zero, p => p.magnitude > 10f));
            Assert.IsNull(UnstuckSolver.FindFreeSpot(Vector2.zero, null));
        }

        [Test]
        public void PauseMenu_HasTheUnstuckButton()
        {
            VisualTreeAsset tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/Resources/UI/PauseMenu.uxml");
            Assert.IsNotNull(tree, "missing Resources/UI/PauseMenu.uxml");
            // PausePanel binds it by name
            Assert.IsNotNull(tree.Instantiate().Q<Button>("Btn_Unstuck"));
        }
    }
}
#endif

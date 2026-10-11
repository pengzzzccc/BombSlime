#if UNITY_INCLUDE_TESTS
using System.Reflection;
using Inkform.Bus;
using Inkform.Interactable;
using Inkform.Interactable.Parts;
using Inkform.Tool;
using NUnit.Framework;
using UnityEngine;

namespace Inkform.Tests
{
    /// <summary>
    /// ExplodePart detonation semantics: target-touch always detonates, world contact only once
    /// spat (armed), chain fires after the fuse, and the whole thing is idempotent. Counted via
    /// HazardBus.Blast — exactly one per explosion, whatever triggered it.
    /// </summary>
    public sealed class InkformExplodePartTests
    {
        [TearDown]
        public void TearDown()
        {
            Time.timeScale = 1f;
            AudioListener.pause = false;
        }

        [Test]
        public void ExplodePart_PlayerTouchDetonatesArmedOrNot()
        {
            GameObject bomb = NewBomb(out ExplodePart part, out _, chainDelay: 1f);
            WithBlastCounter(count =>
            {
                Collider2D player = NewOther("player", Tags.Player);
                try
                {
                    Assert.IsTrue(part.HandleContact(ContactPhase.Enter, player), "player touch is consumed");
                    Assert.AreEqual(1, count(), "player contact detonates an unarmed bomb");

                    // Idempotent: a Stay dispatch the same frame must not fire a second blast
                    part.HandleContact(ContactPhase.Stay, player);
                    Assert.AreEqual(1, count(), "Explode takes effect exactly once");
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(player.gameObject);
                }
            });
        }

        [Test]
        public void ExplodePart_WorldContactIgnoredUntilSpitArmed()
        {
            // The spawner-bomb contract: resting bombs must lie on the terrain without detonating
            GameObject bomb = NewBomb(out ExplodePart part, out _, chainDelay: 1f);
            WithBlastCounter(count =>
            {
                Collider2D ground = NewOther("ground", tag: null, layer: 6);   // Terrain layer
                try
                {
                    Assert.IsFalse(part.HandleContact(ContactPhase.Enter, ground));
                    Assert.AreEqual(0, count(), "unarmed bombs ignore terrain contact");

                    ((IOnSpit)part).OnSpit();       // spat out of the player's mouth
                    Assert.IsTrue(part.HandleContact(ContactPhase.Enter, ground));
                    Assert.AreEqual(1, count(), "an armed bomb detonates on world contact");
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(ground.gameObject);
                }
            });
        }

        [Test]
        public void ExplodePart_ArmedWorldContactRespectsTheLayerMask()
        {
            // The Interactable-mask contract: an armed bomb brushes pickups, checkpoints and doors
            // (Default layer) without popping — only worldDetonatorMask layers (Terrain | Breakable)
            // detonate it
            GameObject bomb = NewBomb(out ExplodePart part, out _, chainDelay: 1f);
            WithBlastCounter(count =>
            {
                Collider2D pickup = NewOther("pickup", tag: null, layer: 0);   // Default — not in the mask
                Collider2D wall = NewOther("wall", tag: null, layer: 6);       // Terrain — in the mask
                try
                {
                    ((IOnSpit)part).OnSpit();       // spat out of the player's mouth

                    Assert.IsFalse(part.HandleContact(ContactPhase.Enter, pickup));
                    Assert.AreEqual(0, count(), "an armed bomb must not detonate on a non-mask layer");

                    Assert.IsTrue(part.HandleContact(ContactPhase.Enter, wall));
                    Assert.AreEqual(1, count(), "an armed bomb still detonates on a mask layer (Terrain)");
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(pickup.gameObject);
                    UnityEngine.Object.DestroyImmediate(wall.gameObject);
                }
            });
        }

        [Test]
        public void ExplodePart_HazardLayerContactDetonatesWithoutArming()
        {
            // Spikes: an explosive does not survive resting on them, spat or not
            GameObject bomb = NewBomb(out ExplodePart part, out _, chainDelay: 1f);
            WithBlastCounter(count =>
            {
                Collider2D spikes = NewOther("spikes", tag: null, layer: 13);   // Hazard layer
                try
                {
                    Assert.IsTrue(part.HandleContact(ContactPhase.Enter, spikes));
                    Assert.AreEqual(1, count(), "hazard-layer contact detonates an unarmed bomb");
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(spikes.gameObject);
                }
            });
        }

        [Test]
        public void ExplodePart_ChainFiresAfterDelay()
        {
            // chainDelay 0 = same-frame chain once Update runs — the Timer holds an absolute end time
            GameObject bomb = NewBomb(out ExplodePart part, out GameObject root, chainDelay: 0f);
            WithBlastCounter(count =>
            {
                HazardBus.RaiseExploded(root, Vector2.zero, 1f);
                Assert.AreEqual(0, count(), "the fuse must burn through Update, not fire inside the event");

                Invoke(part, "Update");
                Assert.AreEqual(1, count(), "a bomb caught in a blast detonates after its fuse");
            });
        }

        [Test]
        public void ExplodePart_ChainReactionOff_IgnoresBlast()
        {
            GameObject bomb = NewBomb(out ExplodePart part, out GameObject root, chainDelay: 0f);
            SetField(part, "chainReaction", false);
            WithBlastCounter(count =>
            {
                HazardBus.RaiseExploded(root, Vector2.zero, 1f);
                Invoke(part, "Update");
                Assert.AreEqual(0, count(), "chainReaction off: a bomb caught in a blast stays put");
            });
            UnityEngine.Object.DestroyImmediate(bomb);   // never exploded, so it did not self-destruct
        }

        // Builds a minimal bomb: Interactable node + ExplodePart + collider. chainDelay is shortened
        // so fuses do not depend on the editor clock; blastMask stays 0 (Nothing) so the overlap in
        // Explode hits nothing and only the overall Blast signal is counted
        private static GameObject NewBomb(out ExplodePart part, out GameObject root, float chainDelay)
        {
            GameObject go = new GameObject("ExplodePart test bomb");
            go.SetActive(false);
            go.AddComponent<CircleCollider2D>();
            go.AddComponent<Inkform.Interactable.Interactable>();
            part = go.AddComponent<ExplodePart>();
            SetField(part, "chainDelay", chainDelay);
            go.SetActive(true);     // Awake attaches parts, OnEnable subscribes the chain listener
            root = go;
            return go;
        }

        private static Collider2D NewOther(string name, string tag, int layer = 0)
        {
            GameObject go = new GameObject(name);
            if (tag != null) go.tag = tag;
            go.layer = layer;
            return go.AddComponent<BoxCollider2D>();
        }

        // Subscribes a Blast counter for the duration of the body; the bomb GameObject self-destructs
        // inside Explode (DestroyImmediate in edit mode), so no explicit cleanup is needed for it
        private static void WithBlastCounter(System.Action<System.Func<int>> body)
        {
            int count = 0;
            void OnBlast(Vector2 center, float radius, float force) => count++;
            HazardBus.Blast += OnBlast;
            try { body(() => count); }
            finally { HazardBus.Blast -= OnBlast; }
        }

        private static void SetField(object target, string name, object value) =>
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(target, value);

        private static void Invoke(object target, string method) =>
            target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(target, null);
    }
}
#endif

#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Reflection;
using Inkform.Bus;
using Inkform.Interactable;
using Inkform.Interactable.Parts;
using Inkform.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Inkform.Tests
{
    /// <summary>
    /// Tutorial hints: the binding-path → icon table (against the shipped TutorialHints asset, so
    /// the ConIcon wiring is pinned too), template splitting, the zone's bus signals and the HUD
    /// view's zone stacking.
    /// </summary>
    public sealed class TutorialHintTests
    {
        private const string SetPath = "Assets/Resources/UI/TutorialHints.asset";
        private TutorialHintSet set;
        private readonly List<Object> spawned = new List<Object>();

        [SetUp]
        public void SetUp()
        {
            set = AssetDatabase.LoadAssetAtPath<TutorialHintSet>(SetPath);
            Assert.IsNotNull(set, "TutorialHints asset missing");
        }

        [TearDown]
        public void TearDown()
        {
            foreach (Object o in spawned) if (o != null) Object.DestroyImmediate(o);
            spawned.Clear();
            typeof(UiBus).GetMethod("ResetStatics", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
        }

        // ---- Icon table ----

        [Test]
        public void PadTrigger_UsesFaceSpecificSprite()
        {
            InputGlyphs.Glyph xbox = InputGlyphs.ResolvePath("<Gamepad>/leftTrigger", "LT", PromptScheme.Xbox, set);
            InputGlyphs.Glyph ps = InputGlyphs.ResolvePath("<Gamepad>/leftTrigger", "L2", PromptScheme.PlayStation, set);

            Assert.AreEqual("ConIcon_4", xbox.sprite.name);    // LT
            Assert.AreEqual("ConIcon_10", ps.sprite.name);     // L2
        }

        [Test]
        public void PadShoulder_UsesFaceSpecificSprite()
        {
            Assert.AreEqual("ConIcon_25", InputGlyphs.ResolvePath("<Gamepad>/rightShoulder", "RB", PromptScheme.Xbox, set).sprite.name);
            Assert.AreEqual("ConIcon_18", InputGlyphs.ResolvePath("<Gamepad>/rightShoulder", "R1", PromptScheme.PlayStation, set).sprite.name);
        }

        [Test]
        public void KeyboardKeys_BecomeShortKeycaps()
        {
            InputGlyphs.Glyph space = InputGlyphs.ResolvePath("<Keyboard>/space", "Space", PromptScheme.KeyboardMouse, set);
            InputGlyphs.Glyph shift = InputGlyphs.ResolvePath("<Keyboard>/leftShift", "Left Shift", PromptScheme.KeyboardMouse, set);

            Assert.IsNull(space.sprite);
            Assert.AreEqual("Space", space.label);
            Assert.AreEqual("Shift", shift.label);
        }

        [Test]
        public void MouseButton_WithoutArt_IsLabelledKeycap()
        {
            InputGlyphs.Glyph lmb = InputGlyphs.ResolvePath("<Mouse>/leftButton", "Left Button", PromptScheme.KeyboardMouse, set);
            Assert.IsNull(lmb.sprite);
            Assert.AreEqual("LMB", lmb.label);
        }

        [Test]
        public void UnmappedPadControl_FallsBackToDisplayName()
        {
            InputGlyphs.Glyph glyph = InputGlyphs.ResolvePath("<Gamepad>/touchpadButton", "Touchpad", PromptScheme.PlayStation, set);
            Assert.IsNull(glyph.sprite);
            Assert.AreEqual("Touchpad", glyph.label);
        }

        [Test]
        public void EveryHint_HasCopyForBothFamilies()
        {
            foreach (TutorialHint hint in System.Enum.GetValues(typeof(TutorialHint)))
            {
                Assert.IsNotEmpty(set.TextFor(hint, false), $"{hint} keyboard copy");
                Assert.IsNotEmpty(set.TextFor(hint, true), $"{hint} gamepad copy");
            }
        }

        // ---- Template ----

        [Test]
        public void Split_SeparatesTextAndTokens()
        {
            List<InputGlyphs.Segment> parts = InputGlyphs.Split("Press {Jump} to jump");

            Assert.AreEqual(3, parts.Count);
            Assert.IsFalse(parts[0].isToken);
            Assert.IsTrue(parts[1].isToken);
            Assert.AreEqual("Jump", parts[1].text);
            Assert.AreEqual(" to jump", parts[2].text);
        }

        [Test]
        public void Split_UnclosedBraceStaysText()
        {
            List<InputGlyphs.Segment> parts = InputGlyphs.Split("Oops {Jump");
            Assert.AreEqual(1, parts.Count);
            Assert.IsFalse(parts[0].isToken);
        }

        // ---- Zone ----

        [Test]
        public void Zone_RaisesShownOnEnterAndHiddenOnLastExit()
        {
            var zoneObject = new GameObject("Zone");
            spawned.Add(zoneObject);
            zoneObject.AddComponent<BoxCollider2D>().isTrigger = true;
            TutorialHintPart zone = zoneObject.AddComponent<TutorialHintPart>();

            var playerObject = new GameObject("Player") { tag = "Player" };
            spawned.Add(playerObject);
            Collider2D feet = playerObject.AddComponent<BoxCollider2D>();
            Collider2D body = playerObject.AddComponent<CircleCollider2D>();

            int shown = 0, hidden = 0;
            UiBus.TutorialShown += (owner, hint, max) => shown++;
            UiBus.TutorialHidden += owner => hidden++;

            zone.HandleContact(ContactPhase.Enter, feet);
            zone.HandleContact(ContactPhase.Enter, body);
            zone.HandleContact(ContactPhase.Exit, feet);
            Assert.AreEqual(1, shown);
            Assert.AreEqual(0, hidden, "one player collider still inside");

            zone.HandleContact(ContactPhase.Exit, body);
            Assert.AreEqual(1, hidden);
        }

        // ---- HUD view ----

        [Test]
        public void View_LatestZoneWins_AndLeavingItRevealsTheOneBeneath()
        {
            var hud = new VisualElement();
            var line = new VisualElement { name = "TutorialHint" };
            line.Add(new VisualElement { name = "TutorialHintPanel" });
            hud.Add(line);

            var view = new TutorialHintView(hud);
            object a = new object(), b = new object();
            try
            {
                UiBus.RaiseTutorialShown(a, TutorialHint.Jump, 0f);
                view.Tick(1f);
                Assert.AreEqual(TutorialHint.Jump, view.Current);

                // B on top: Jump fades out first, then Dash is built and fades in
                UiBus.RaiseTutorialShown(b, TutorialHint.Dash, 0f);
                view.Tick(1f);
                view.Tick(1f);
                Assert.AreEqual(TutorialHint.Dash, view.Current);

                UiBus.RaiseTutorialHidden(a);
                view.Tick(1f);
                Assert.AreEqual(TutorialHint.Dash, view.Current, "leaving the zone beneath changes nothing");

                UiBus.RaiseTutorialHidden(b);
                view.Tick(1f);
                Assert.IsNull(view.Current);
                Assert.AreEqual(0f, view.Opacity);
            }
            finally
            {
                view.Dispose();
            }
        }

        [Test]
        public void View_MaxSecondsFadesOutWhileStillInside()
        {
            var hud = new VisualElement();
            var line = new VisualElement { name = "TutorialHint" };
            line.Add(new VisualElement { name = "TutorialHintPanel" });
            hud.Add(line);

            var view = new TutorialHintView(hud);
            try
            {
                UiBus.RaiseTutorialShown(new object(), TutorialHint.Bomb, 2f);
                view.Tick(1f);
                Assert.AreEqual(TutorialHint.Bomb, view.Current);

                view.Tick(1.5f);   // past the 2 s cap
                view.Tick(1f);
                Assert.IsNull(view.Current);
            }
            finally
            {
                view.Dispose();
            }
        }
    }
}
#endif

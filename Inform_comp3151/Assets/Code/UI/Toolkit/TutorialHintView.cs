using System.Collections.Generic;
using Inkform.Bus;
using Inkform.Input;
using Inkform.Settings;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace Inkform.UI
{
    /// <summary>
    /// The tutorial hint line at the bottom of the screen: large text with the action's button
    /// icons inline, no plate (a deep text shadow carries it over the level), fading in while a
    /// TutorialHintPart zone holds it and out when the player leaves.
    ///
    /// Zones stack — the latest one entered wins, leaving it reveals the one beneath. A change of
    /// hint fades the old line out before the new one fades in. A device switch (pad face included)
    /// or a rebind rebuilds the line in place, no fade, so the icons always match the live input.
    ///
    /// A plain object owned and ticked by Hud on unscaled time; it hides with the Gameplay layer.
    /// </summary>
    public sealed class TutorialHintView
    {
        private const float FadeSeconds = 0.35f;
        private const float RisePixels = 10f;  // fading in drifts the line up into place

        private struct Request
        {
            public object owner;
            public TutorialHint hint;
            public float maxSeconds;
        }

        private readonly VisualElement root;
        private readonly VisualElement panel;
        private readonly TutorialHintSet set;
        private readonly List<Request> requests = new List<Request>();
        private readonly List<InputGlyphs.Glyph> glyphs = new List<InputGlyphs.Glyph>();

        private bool built;
        private TutorialHint builtHint;
        private PromptScheme builtScheme;
        private bool bindingsDirty;

        private object topOwner;
        private float topElapsed;
        private float opacity;

        /// <summary>The hint currently on screen (null while nothing is built or fully faded).</summary>
        public TutorialHint? Current => built && opacity > 0f ? builtHint : (TutorialHint?)null;
        public float Opacity => opacity;

        public TutorialHintView(VisualElement hudRoot)
        {
            root = hudRoot?.Q("TutorialHint");
            panel = root?.Q("TutorialHintPanel") ?? root;
            set = TutorialHintSet.Load();
            if (root == null) return;
            if (set == null)
                Debug.LogWarning($"TutorialHintView: missing Resources/{TutorialHintSet.ResourcePath} — tutorial hints show text only");

            ApplyOpacity();

            UiBus.TutorialShown += OnShown;
            UiBus.TutorialHidden += OnHidden;
            SettingsStore.Changed += OnSettingsChanged;
        }

        public void Dispose()
        {
            UiBus.TutorialShown -= OnShown;
            UiBus.TutorialHidden -= OnHidden;
            SettingsStore.Changed -= OnSettingsChanged;
        }

        // ---- Requests ----

        private void OnShown(object owner, TutorialHint hint, float maxSeconds)
        {
            RemoveOwner(owner);
            requests.Add(new Request { owner = owner, hint = hint, maxSeconds = maxSeconds });
        }

        private void OnHidden(object owner) => RemoveOwner(owner);

        private void RemoveOwner(object owner)
        {
            for (int i = requests.Count - 1; i >= 0; i--)
                if (ReferenceEquals(requests[i].owner, owner)) requests.RemoveAt(i);
        }

        // Rebinds and device switches both land here; the icons are rebuilt on the next tick
        private void OnSettingsChanged() => bindingsDirty = true;

        // ---- Per-frame ----

        public void Tick(float unscaledDelta)
        {
            if (root == null) return;

            bool hasTop = requests.Count > 0;
            Request top = hasTop ? requests[requests.Count - 1] : default;

            // A zone's display cap counts from the moment it became the top request
            if (!hasTop || !ReferenceEquals(top.owner, topOwner))
            {
                topOwner = hasTop ? top.owner : null;
                topElapsed = 0f;
            }
            else
            {
                topElapsed += unscaledDelta;
            }

            bool timedOut = hasTop && top.maxSeconds > 0f && topElapsed >= top.maxSeconds;
            bool wantVisible = hasTop && !timedOut;

            PromptScheme scheme = InteractPromptIcons.DetectCurrent();
            if (wantVisible)
            {
                if (!built || top.hint != builtHint)
                {
                    // Different hint: fade the old line out first, build the new one once invisible
                    if (opacity <= 0f) Build(top.hint, scheme);
                    else wantVisible = false;
                }
                else if (scheme != builtScheme || bindingsDirty)
                {
                    Build(top.hint, scheme);   // same hint, new device or bindings: swap icons in place
                }
            }

            float step = FadeSeconds > 0f ? unscaledDelta / FadeSeconds : 1f;
            opacity = Mathf.MoveTowards(opacity, wantVisible ? 1f : 0f, step);
            ApplyOpacity();
        }

        private void ApplyOpacity()
        {
            root.style.opacity = opacity;
            root.style.display = opacity > 0f ? DisplayStyle.Flex : DisplayStyle.None;
            root.style.translate = new Translate(0f, (1f - opacity) * RisePixels);
        }

        // ---- Building the line ----

        private void Build(TutorialHint hint, PromptScheme scheme)
        {
            built = true;
            builtHint = hint;
            builtScheme = scheme;
            bindingsDirty = false;

            panel.Clear();
            string template = set != null ? set.TextFor(hint, scheme != PromptScheme.KeyboardMouse) : hint.ToString();
            foreach (InputGlyphs.Segment segment in InputGlyphs.Split(template))
            {
                if (!segment.isToken)
                {
                    AddText(segment.text);
                    continue;
                }

                glyphs.Clear();
                InputAction action = InputActions.Wrapper.asset.FindAction(segment.text);
                InputGlyphs.Resolve(action, scheme, set, glyphs);
                if (glyphs.Count == 0)
                {
                    AddText(segment.text);   // unknown action name: show the word rather than a gap
                    continue;
                }
                foreach (InputGlyphs.Glyph glyph in glyphs) AddGlyph(glyph);
            }
        }

        private void AddText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            string trimmed = text.Trim();
            var label = new Label(trimmed) { pickingMode = PickingMode.Ignore };
            label.AddToClassList("hud-tutorial-text");   // carries its own deeper shadow (Hud.uss)
            // ", hold to jump higher" follows its icon directly, like the comma would in prose
            if (char.IsPunctuation(trimmed[0])) label.style.marginLeft = 0f;
            panel.Add(label);
        }

        // Same icon / keycap look as the menu prompts and the rebind table (Theme.uss .key-*),
        // at the large size to sit with the large text
        private void AddGlyph(InputGlyphs.Glyph glyph) => panel.Add(GlyphElements.Create(glyph, GlyphSize.Large));
    }
}

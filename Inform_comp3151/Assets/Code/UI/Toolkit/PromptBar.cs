using System;
using System.Collections.Generic;
using Inkform.Bus;
using UnityEngine.UIElements;

namespace Inkform.UI
{
    /// <summary>
    /// The menu sheets' bottom-right prompt row: "[icon] Confirm  [icon] Back", drawn for the
    /// device in hand — Enter/Esc keycaps on keyboard, the pad's own face buttons (A/B or ✕/○)
    /// on a controller. Menu navigation is not rebindable, so each prompt names fixed controls;
    /// the icons come from the same table as the tutorial hints (InputGlyphs + TutorialHintSet).
    ///
    /// A prompt given an action is also a mouse target (click "Back" = press Esc). The bar
    /// polls the device on the element's own scheduler, so no panel has to tick it.
    /// </summary>
    public sealed class PromptBar
    {
        /// <summary>The menu controls a prompt can show.</summary>
        public enum Key
        {
            Confirm,
            Back,
            Overwrite,
            Change,
            Select,
            Scroll
        }

        private struct Entry
        {
            public Key key;
            public string label;
            public Action onClick;
        }

        private const long PollMilliseconds = 200;

        private readonly VisualElement host;
        private readonly TutorialHintSet set;
        private readonly List<Entry> entries = new List<Entry>();
        private readonly List<InputGlyphs.Glyph> glyphs = new List<InputGlyphs.Glyph>();
        private PromptScheme? builtScheme;

        public PromptBar(VisualElement host)
        {
            this.host = host;
            set = TutorialHintSet.Load();
            host?.schedule.Execute(Poll).Every(PollMilliseconds);
        }

        /// <summary>Appends a prompt; onClick makes it clickable with the mouse.</summary>
        public PromptBar Add(Key key, string label, Action onClick = null)
        {
            entries.Add(new Entry { key = key, label = label, onClick = onClick });
            builtScheme = null;
            Poll();
            return this;
        }

        /// <summary>Replaces every prompt (the options sheet swaps them between its levels).</summary>
        public void Clear()
        {
            entries.Clear();
            builtScheme = null;
            Poll();
        }

        private void Poll()
        {
            if (host == null) return;
            PromptScheme scheme = InteractPromptIcons.DetectCurrent();
            if (builtScheme == scheme) return;
            builtScheme = scheme;
            Build(scheme);
        }

        private void Build(PromptScheme scheme)
        {
            host.Clear();
            foreach (Entry entry in entries)
            {
                // A prompt with no control on this device (Change on a pad: the stick that
                // selects also changes) is left out
                glyphs.Clear();
                ResolveKey(entry.key, scheme, glyphs);
                if (glyphs.Count == 0) continue;

                var item = new VisualElement();
                item.AddToClassList("prompt");
                if (entry.onClick != null)
                {
                    item.AddToClassList("prompt--clickable");
                    Action onClick = entry.onClick;
                    item.AddManipulator(new Clickable(() =>
                    {
                        UiBus.RaiseClicked();
                        onClick();
                    }));
                }
                else
                {
                    item.pickingMode = PickingMode.Ignore;
                }

                foreach (InputGlyphs.Glyph glyph in glyphs) item.Add(GlyphElements.Create(glyph, GlyphSize.Small));

                var label = new Label(entry.label) { pickingMode = PickingMode.Ignore };
                label.AddToClassList("prompt-label");
                label.AddToClassList("outline");
                item.Add(label);

                host.Add(item);
            }
        }

        // Fixed menu controls (UIManager polls Esc / pad East for Back; Submit is the UI map's)
        private void ResolveKey(Key key, PromptScheme scheme, List<InputGlyphs.Glyph> into)
        {
            bool pad = scheme != PromptScheme.KeyboardMouse;
            switch (key)
            {
                case Key.Confirm:
                    into.Add(pad ? Pad("buttonSouth", scheme) : Kb("enter", "Enter", scheme));
                    break;
                case Key.Back:
                    into.Add(pad ? Pad("buttonEast", scheme) : Kb("escape", "Esc", scheme));
                    break;
                case Key.Overwrite:
                    into.Add(pad ? Pad("buttonNorth", scheme) : Kb("ctrl", "Ctrl", scheme));
                    break;
                case Key.Change:
                    // On a pad the left stick both selects and changes (shown once, as Select)
                    if (!pad)
                    {
                        into.Add(Kb("leftArrow", "←", scheme));
                        into.Add(Kb("rightArrow", "→", scheme));
                    }
                    break;
                case Key.Select:
                    if (pad)
                    {
                        into.Add(Pad("leftStick", scheme));
                    }
                    else
                    {
                        into.Add(Kb("upArrow", "↑", scheme));
                        into.Add(Kb("downArrow", "↓", scheme));
                    }
                    break;
                case Key.Scroll:
                    into.Add(pad ? Pad("rightStick", scheme)
                        : InputGlyphs.ResolvePath("<Mouse>/scroll", "Wheel", scheme, set));
                    break;
            }
        }

        private InputGlyphs.Glyph Pad(string control, PromptScheme scheme) =>
            InputGlyphs.ResolvePath($"<Gamepad>/{control}", control, scheme, set);

        private InputGlyphs.Glyph Kb(string control, string display, PromptScheme scheme) =>
            InputGlyphs.ResolvePath($"<Keyboard>/{control}", display, scheme, set);
    }
}

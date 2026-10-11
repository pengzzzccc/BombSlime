using System.Collections.Generic;
using Inkform.Input;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Inkform.UI
{
    /// <summary>
    /// Turns an input action into the icons a hint draws for it, from the live bindings: the
    /// device family picks the binding group, effectivePath carries the player's remaps, and the
    /// pad face (PlayStation / Xbox) picks the sprite. Keyboard keys and anything without art
    /// become a keycap carrying the key's name, so a hint never shows a blank.
    /// </summary>
    public static class InputGlyphs
    {
        /// <summary>One icon: a sprite (pad button art), or a keycap with a label when sprite is null.</summary>
        public struct Glyph
        {
            public Sprite sprite;
            public string label;
        }

        /// <summary>One piece of a hint template: plain text, or a {Token} naming an action.</summary>
        public struct Segment
        {
            public bool isToken;
            public string text;
        }

        // Composite parts shown for a keyboard axis (Move's WASD): a side-scroller only walks left/right
        private static readonly string[] AxisParts = { "left", "right" };

        /// <summary>Appends the icons for an action's binding in the current scheme's group: its
        /// first plain binding, or for a composite (keyboard Move) its left and right keys.</summary>
        public static void Resolve(InputAction action, PromptScheme scheme, TutorialHintSet set, List<Glyph> into)
        {
            if (action == null) return;
            string group = scheme == PromptScheme.KeyboardMouse ? BindingTools.KbmGroup : BindingTools.GamepadGroup;

            int index = BindingTools.FindBindingIndex(action, group, null);
            if (index >= 0)
            {
                into.Add(ResolvePath(action.bindings[index].effectivePath, action.GetBindingDisplayString(index), scheme, set));
                return;
            }

            foreach (string part in AxisParts)
            {
                int partIndex = BindingTools.FindBindingIndex(action, group, part);
                if (partIndex < 0) continue;
                into.Add(ResolvePath(action.bindings[partIndex].effectivePath, action.GetBindingDisplayString(partIndex), scheme, set));
            }
        }

        /// <summary>Pure mapping from one binding path to its icon (no live devices or actions), so
        /// tests can pin the table.</summary>
        public static Glyph ResolvePath(string path, string display, PromptScheme scheme, TutorialHintSet set)
        {
            SplitPath(path, out string device, out string control);

            if (device == "Mouse" || device == "Pointer")
            {
                switch (control)
                {
                    case "leftButton": return SpriteOrKey(set != null ? set.MouseLeft : null, "LMB");
                    case "rightButton": return SpriteOrKey(set != null ? set.MouseRight : null, "RMB");
                    case "middleButton": return Key("MMB");
                    case "delta":
                    case "position": return Key("Mouse");
                }
            }
            else if (device != "Keyboard" && set != null && scheme != PromptScheme.KeyboardMouse)
            {
                Sprite sprite = set.PadSprite(control, scheme);
                if (sprite != null) return new Glyph { sprite = sprite };
            }

            return Key(ShortKeyName(display, control));
        }

        /// <summary>Splits "Press {Jump} to jump" into text / token / text. An unclosed brace is
        /// kept as text.</summary>
        public static List<Segment> Split(string template)
        {
            var segments = new List<Segment>();
            if (string.IsNullOrEmpty(template)) return segments;

            int i = 0;
            while (i < template.Length)
            {
                int open = template.IndexOf('{', i);
                int close = open >= 0 ? template.IndexOf('}', open + 1) : -1;
                if (open < 0 || close < 0)
                {
                    segments.Add(new Segment { text = template.Substring(i) });
                    break;
                }
                if (open > i) segments.Add(new Segment { text = template.Substring(i, open - i) });
                segments.Add(new Segment { isToken = true, text = template.Substring(open + 1, close - open - 1).Trim() });
                i = close + 1;
            }
            return segments;
        }

        // "<Gamepad>/dpad/left" -> device "Gamepad", control "dpad/left"
        private static void SplitPath(string path, out string device, out string control)
        {
            device = string.Empty;
            control = path ?? string.Empty;
            if (string.IsNullOrEmpty(path) || path[0] != '<') return;

            int end = path.IndexOf('>');
            if (end < 0) return;
            device = path.Substring(1, end - 1);
            control = end + 2 <= path.Length ? path.Substring(end + 2) : string.Empty;
        }

        // Keycaps stay short: "Left Shift" -> "Shift"; an empty display falls back to the control name
        private static string ShortKeyName(string display, string control)
        {
            string name = string.IsNullOrEmpty(display) ? control : display;
            if (name.StartsWith("Left ")) name = name.Substring(5);
            else if (name.StartsWith("Right ")) name = name.Substring(6);
            return string.IsNullOrEmpty(name) ? "?" : name;
        }

        private static Glyph SpriteOrKey(Sprite sprite, string label) =>
            sprite != null ? new Glyph { sprite = sprite } : Key(label);

        private static Glyph Key(string label) => new Glyph { label = label };
    }
}

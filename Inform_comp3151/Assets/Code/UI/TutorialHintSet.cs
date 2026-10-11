using System;
using Inkform.Bus;
using UnityEngine;

namespace Inkform.UI
{
    /// <summary>
    /// Copy and gamepad icons for the tutorial hints (Resources/UI/TutorialHints). Each hint has a
    /// keyboard line and a gamepad line; {ActionName} in a line (an action of the Player map:
    /// Move, Aim, Jump, Dash, RopeFire, SpitBomb...) becomes that action's live binding icon, so a
    /// rebind or a device switch changes the icon, never the copy.
    ///
    /// Gamepad icons are keyed by the binding's control path after the device ("leftTrigger",
    /// "dpad/left"), one sprite per pad face. Keyboard keys need no art: they draw as keycaps.
    /// </summary>
    [CreateAssetMenu(menuName = "Inkform/Tutorial Hint Set", fileName = "TutorialHints")]
    public class TutorialHintSet : ScriptableObject
    {
        public const string ResourcePath = "UI/TutorialHints";

        [Serializable]
        public class HintCopy
        {
            public TutorialHint hint;
            [TextArea] public string keyboardText;
            [TextArea] public string gamepadText;
        }

        [Serializable]
        public class PadIcon
        {
            [Tooltip("Control path after the device, as in the input asset: leftTrigger, buttonSouth, dpad/left...")]
            public string control;
            public Sprite xbox;
            public Sprite playStation;
        }

        [SerializeField] private HintCopy[] hints = new HintCopy[0];
        [SerializeField] private PadIcon[] gamepadIcons = new PadIcon[0];
        [Tooltip("Optional mouse-button art; empty draws an LMB / RMB keycap")]
        [SerializeField] private Sprite mouseLeft;
        [SerializeField] private Sprite mouseRight;

        public Sprite MouseLeft => mouseLeft;
        public Sprite MouseRight => mouseRight;

        public static TutorialHintSet Load() => Resources.Load<TutorialHintSet>(ResourcePath);

        /// <summary>The hint's line for the device family; empty when the hint has no copy.</summary>
        public string TextFor(TutorialHint hint, bool gamepad)
        {
            foreach (HintCopy copy in hints)
            {
                if (copy == null || copy.hint != hint) continue;
                return (gamepad ? copy.gamepadText : copy.keyboardText) ?? string.Empty;
            }
            return string.Empty;
        }

        /// <summary>The pad face's sprite for a control, or null when the control is not mapped
        /// (the caller falls back to a labelled keycap).</summary>
        public Sprite PadSprite(string control, PromptScheme scheme)
        {
            foreach (PadIcon icon in gamepadIcons)
            {
                if (icon == null || !string.Equals(icon.control, control, StringComparison.OrdinalIgnoreCase)) continue;
                return scheme == PromptScheme.PlayStation ? icon.playStation : icon.xbox;
            }
            return null;
        }
    }
}

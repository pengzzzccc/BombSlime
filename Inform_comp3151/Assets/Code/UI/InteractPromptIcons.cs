using UnityEngine;
using UnityEngine.InputSystem;
using Inkform.Settings;

namespace Inkform.UI
{
    /// <summary>
    /// Which button-face a prompt should draw for — the three families this game promises to support.
    /// Kept separate from SettingsStore.InputDevice (which only splits KeyboardMouse/Gamepad for input
    /// filtering) because a prompt also cares WHICH pad: PlayStation pads letter their north button
    /// with a triangle, Xbox pads with a Y.
    /// </summary>
    public enum PromptScheme
    {
        KeyboardMouse,
        PlayStation,
        Xbox
    }

    /// <summary>
    /// Device-scheme detection, plus the generated keycap the world-space interaction prompt draws
    /// keyboard keys on (pad buttons use the ConIcon art through InputGlyphs / TutorialHintSet).
    ///
    /// Detection follows the same philosophy as InputHandler's auto-switch: the Controls tab family
    /// decides keyboard-vs-pad, the live Gamepad.current decides PS-vs-Xbox (type-name matching — the
    /// concrete pad classes live in Input System sub-namespaces, a string match avoids hard references
    /// to layouts that vary per platform package).
    /// </summary>
    public static class InteractPromptIcons
    {
        // Default Interact keys, used only when the live binding cannot be resolved
        public const string KeyboardGlyph = "E";
        public const string XboxGlyph = "Y";

        public static PromptScheme DetectCurrent()
        {
            if (SettingsStore.Device == SettingsStore.InputDevice.KeyboardMouse) return PromptScheme.KeyboardMouse;
            return Resolve(SettingsStore.Device, Gamepad.current != null ? Gamepad.current.GetType().Name : null);
        }

        /// <summary>Pure overload (no live devices) so tests can pin the mapping table. The family
        /// type is spelled SettingsStore.InputDevice — bare InputDevice would be ambiguous against
        /// UnityEngine.InputSystem.InputDevice in any file importing both namespaces.</summary>
        public static PromptScheme Resolve(SettingsStore.InputDevice family, string gamepadTypeName)
        {
            if (family == SettingsStore.InputDevice.KeyboardMouse) return PromptScheme.KeyboardMouse;

            // Unknown/absent pad defaults to the Xbox face (letters) — it is also what most
            // Switch/generic pads actually print on their north button's neighbours.
            if (string.IsNullOrEmpty(gamepadTypeName)) return PromptScheme.Xbox;

            foreach (string marker in PlayStationMarkers)
            {
                if (gamepadTypeName.Contains(marker)) return PromptScheme.PlayStation;
            }
            return PromptScheme.Xbox;
        }

        private static readonly string[] PlayStationMarkers = { "DualSense", "DualShock", "PlayStation", "PS3", "PS4", "PS5" };

        // ---- Generated placeholder icons: white alpha masks, Point-filtered to match the pixel look ----

        private const int ShapeSize = 48;

        private static Sprite keycapSprite;

        // The generated sprites are runtime Texture2Ds, destroyed when play mode ends — the lazy
        // getters' Unity null check rebuilds them on their own. Clearing the caches here anyway keeps
        // this class on the same ResetStatics discipline as the buses (and releases the textures
        // promptly between runs instead of on first use)
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            keycapSprite = null;
        }

        /// <summary>Rounded-square keycap for keyboard letters.</summary>
        public static Sprite KeycapSprite => keycapSprite != null ? keycapSprite : keycapSprite = BuildRoundedRect(6);

        private static Sprite BuildRoundedRect(int radius)
        {
            var texture = new Texture2D(ShapeSize, ShapeSize, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Point
            };

            for (int y = 0; y < ShapeSize; y++)
            {
                for (int x = 0; x < ShapeSize; x++)
                {
                    // Squared distance to the nearest corner-circle centre: inside the rounded rect
                    // when that centre is within radius — classic SDF rounded box
                    int cx = Mathf.Clamp(x, radius, ShapeSize - 1 - radius);
                    int cy = Mathf.Clamp(y, radius, ShapeSize - 1 - radius);
                    int dx = x - cx, dy = y - cy;
                    bool inside = dx * dx + dy * dy <= radius * radius;
                    texture.SetPixel(x, y, inside ? Color.white : Color.clear);
                }
            }
            return Finish(texture);
        }

        private static Sprite Finish(Texture2D texture)
        {
            texture.Apply(false, true);
            return Sprite.Create(texture, new Rect(0f, 0f, ShapeSize, ShapeSize),
                new Vector2(0.5f, 0.5f), ShapeSize);
        }
    }
}

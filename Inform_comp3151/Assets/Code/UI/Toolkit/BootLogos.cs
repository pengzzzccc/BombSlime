using Inkform.Audio;
using UnityEngine;

namespace Inkform.UI
{
    /// <summary>
    /// Boot sequence content: the studio card, the game logo (the main menu's title screen, which
    /// then moves aside for the menu) and the sting that plays as the studio card fades in. Every
    /// slot is deliberately optional — an empty sprite degrades to a text placeholder ("STUDIO
    /// LOGO" / gameTitle) and an empty sting stays silent, so the whole sequence runs before any
    /// art exists. Drop real assets into Resources/UI/BootLogos.asset
    /// and the code never changes.
    /// </summary>
    [CreateAssetMenu(menuName = "Inkform/Boot Logos", fileName = "BootLogos")]
    public class BootLogos : ScriptableObject
    {
        [Tooltip("Studio card art. Empty = text placeholder.")]
        public Sprite studioLogo;

        [Tooltip("Game logo on the title screen and beside the main menu. Pixel art: shown at whole multiples (x6, x7 of 64 px). Empty = text placeholder built from gameTitle.")]
        public Sprite gameLogo;

        [Tooltip("One-shot sting played as the studio card starts fading in. Empty = silent.")]
        public SoundCue sting;

        [Tooltip("Placeholder / fallback text for the game logo.")]
        public string gameTitle = "BOMB SLIME";

        [Tooltip("Studio card: full-visible dwell before the fade-out, in seconds.")]
        public float studioHoldSeconds = 2f;

        [Tooltip("Studio card fade in/out and the boot sheet's closing fade into the title screen, in seconds.")]
        public float fadeSeconds = 0.4f;
    }
}

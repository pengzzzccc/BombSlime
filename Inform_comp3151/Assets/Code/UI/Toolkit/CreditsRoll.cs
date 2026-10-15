using System;
using System.Collections.Generic;
using Inkform.Audio;
using UnityEngine;

namespace Inkform.UI
{
    /// <summary>
    /// What the end credits roll shows, top to bottom, and how it moves (Resources/UI/CreditsRoll,
    /// read by CreditsPanel). Names, art and pacing are content — edit them here in the
    /// Inspector; the code never changes. The roll opens with the game logo (BootLogos) and
    /// ends on finalImage, which stops in the middle of the screen before the fade out.
    /// </summary>
    [CreateAssetMenu(menuName = "Inkform/Credits Roll", fileName = "CreditsRoll")]
    public class CreditsRoll : ScriptableObject
    {
        public const string ResourcePath = "UI/CreditsRoll";

        public enum EntryKind
        {
            Names,   // a role heading over its people
            Image,   // a piece of art (concept paintings)
            Line     // one line of text on its own ("Thanks for playing")
        }

        [Serializable]
        public class Entry
        {
            public EntryKind kind = EntryKind.Names;
            [Tooltip("Names: the role heading. Line: the text itself")]
            public string title = "";
            [Tooltip("Names only: the people under the heading, one per line")]
            public string[] names = new string[0];
            [Tooltip("Image only")]
            public Texture2D image;
        }

        [Tooltip("Music for the roll, through the music channel. Empty = silence")]
        public SoundCue music;

        [Tooltip("Scroll speed in reference pixels per second (the UI is laid out at 1920x1080)")]
        public float scrollSpeed = 90f;

        [Tooltip("Seconds the final image holds in the middle of the screen before the fade")]
        public float holdSeconds = 1f;

        [Tooltip("Seconds of the closing fade to black (the music fades with it)")]
        public float fadeSeconds = 1.2f;

        public List<Entry> entries = new List<Entry>();

        [Tooltip("The last picture: the roll stops with it centred, then fades out")]
        public Texture2D finalImage;

        public static CreditsRoll Load() => Resources.Load<CreditsRoll>(ResourcePath);
    }
}

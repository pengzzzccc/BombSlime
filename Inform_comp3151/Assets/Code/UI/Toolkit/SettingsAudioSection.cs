using Inkform.Settings;
using UnityEngine.UIElements;

namespace Inkform.UI
{
    /// <summary>
    /// The AUDIO section: mute plus the three volume bars. Bars push into SettingsStore on change
    /// and are pulled back (without notify) on Refresh — never both, or a drag would feed back
    /// into itself.
    /// </summary>
    public class SettingsAudioSection : SettingsSection
    {
        public SettingsAudioSection(SettingsPanel owner, VisualElement rows) : base(owner, rows, "AUDIO")
        {
            AddOnOffRow("Mute", () => SettingsStore.Muted, SettingsStore.SetMuted);
            AddSliderRow("Main Volume", 0f, 1f, Percent, () => SettingsStore.MasterVolume, SettingsStore.SetMasterVolume);
            AddSliderRow("Music Volume", 0f, 1f, Percent, () => SettingsStore.MusicVolume, SettingsStore.SetMusicVolume);
            AddSliderRow("SFX Volume", 0f, 1f, Percent, () => SettingsStore.SfxVolume, SettingsStore.SetSfxVolume);
        }
    }
}

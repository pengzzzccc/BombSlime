using System.Collections.Generic;
using Inkform.Settings;
using UnityEngine;
using UnityEngine.UIElements;

namespace Inkform.UI
{
    /// <summary>
    /// The VIDEO section: resolution, fullscreen, vsync, fps cap, FX intensity, the HUD's run
    /// timer and fps counter, the perf recorder tools. List rows match their stored value by value (not index) — the machine's resolution
    /// list differs per player.
    /// </summary>
    public class SettingsVideoSection : SettingsSection
    {
        public SettingsVideoSection(SettingsPanel owner, VisualElement rows) : base(owner, rows, "VIDEO")
        {
            AddChoiceRow("Resolution",
                () => SettingsStore.AvailableResolutions.Count,
                () => IndexOfResolution(SettingsStore.AvailableResolutions, SettingsStore.ResolutionWidth, SettingsStore.ResolutionHeight),
                i =>
                {
                    Resolution r = SettingsStore.AvailableResolutions[i];
                    SettingsStore.SetResolution(r.width, r.height);
                },
                i =>
                {
                    Resolution r = SettingsStore.AvailableResolutions[i];
                    return FormatRes(r.width, r.height);
                });

            AddOnOffRow("Fullscreen", () => SettingsStore.Fullscreen, SettingsStore.SetFullscreen);
            AddOnOffRow("VSync", () => SettingsStore.VSync, SettingsStore.SetVSync);
            AddListRow("FPS Cap", SettingsStore.FpsOptions, () => SettingsStore.FpsCap, SettingsStore.SetFpsCap, FpsText);
            AddSliderRow("FX Intensity", 0f, 1f, Percent, () => SettingsStore.FxIntensity, SettingsStore.SetFxIntensity);
            AddOnOffRow("Show Timer", () => SettingsStore.ShowTimer, SettingsStore.SetShowTimer);
            AddOnOffRow("Show FPS", () => SettingsStore.ShowFps, SettingsStore.SetShowFps);
            AddOnOffRow("Perf Recording", () => SettingsStore.PerfRecording, SettingsStore.SetPerfRecording);
            AddListRow("Perf Rate", SettingsStore.PerfIntervals, () => SettingsStore.PerfInterval, SettingsStore.SetPerfInterval, RateText);
        }

        /// <summary>The stored size's place in the machine's list; the first entry if it is not
        /// one of them.</summary>
        private static int IndexOfResolution(IReadOnlyList<Resolution> list, int width, int height)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].width == width && list[i].height == height) return i;
            }
            return 0;
        }
    }
}

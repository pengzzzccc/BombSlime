using UnityEngine;

namespace Inkform.UI
{
    /// <summary>
    /// The run's play time as every screen prints it — HUD timer, save cards, end sheet:
    /// minutes:seconds:milliseconds ("03:27:451"). Minutes keep counting past an hour ("75:03:120")
    /// and the milliseconds are truncated, so the readout never runs ahead of the clock.
    /// </summary>
    public static class RunTimeFormat
    {
        public static string Format(float seconds)
        {
            long ms = (long)(Mathf.Max(0f, seconds) * 1000.0);
            long minutes = ms / 60000;
            long secs = ms / 1000 % 60;
            long millis = ms % 1000;
            return $"{minutes:00}:{secs:00}:{millis:000}";
        }
    }
}

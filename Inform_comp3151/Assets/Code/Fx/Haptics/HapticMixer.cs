using System.Collections.Generic;

namespace Inkform.Fx.Haptics
{
    /// <summary>
    /// Device-neutral motor mixing: every rumble is a clip with an optional delay, a duration,
    /// and a linear envelope per motor (start level → end level), so a single clip can swell,
    /// fade or cross from the low motor to the high one. Each frame the live clips are sampled
    /// and max-mixed per motor — a short tap never cuts a longer rumble.
    ///
    /// Tick(0) samples without advancing, which is how a freeze (pause / hitstop) keeps every
    /// clip's remaining delay and duration intact. Pure C#: the director owns the clock.
    /// </summary>
    public sealed class HapticMixer
    {
        private struct Clip
        {
            public float delay;
            public float duration;
            public float elapsed;
            public float lowStart, lowEnd, highStart, highEnd;
        }

        private readonly List<Clip> clips = new List<Clip>();

        public int Count => clips.Count;

        /// <summary>Constant levels for duration seconds, after delay seconds.</summary>
        public void Add(float low, float high, float duration, float delay = 0f) =>
            Add(low, low, high, high, duration, delay);

        /// <summary>Linear envelopes: each motor runs from its start to its end level.</summary>
        public void Add(float lowStart, float lowEnd, float highStart, float highEnd, float duration, float delay = 0f)
        {
            if (duration <= 0f) return;
            clips.Add(new Clip
            {
                delay = delay > 0f ? delay : 0f,
                duration = duration,
                lowStart = lowStart, lowEnd = lowEnd,
                highStart = highStart, highEnd = highEnd,
            });
        }

        public void Clear() => clips.Clear();

        /// <summary>Advances every clip by deltaTime, drops finished ones, and returns the
        /// per-motor maximum of what is sounding now.</summary>
        public void Tick(float deltaTime, out float low, out float high)
        {
            low = 0f;
            high = 0f;
            for (int i = clips.Count - 1; i >= 0; i--)
            {
                Clip clip = clips[i];
                if (clip.delay > 0f)
                {
                    clip.delay -= deltaTime;
                    if (clip.delay > 0f) { clips[i] = clip; continue; }
                    clip.elapsed = -clip.delay;   // the overshoot past the delay already counts
                    clip.delay = 0f;
                }
                else
                {
                    clip.elapsed += deltaTime;
                }

                if (clip.elapsed >= clip.duration)
                {
                    clips.RemoveAt(i);
                    continue;
                }
                clips[i] = clip;

                float t = clip.elapsed / clip.duration;
                float l = clip.lowStart + (clip.lowEnd - clip.lowStart) * t;
                float h = clip.highStart + (clip.highEnd - clip.highStart) * t;
                if (l > low) low = l;
                if (h > high) high = h;
            }
        }
    }
}

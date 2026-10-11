using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Inkform.UI
{
    /// <summary>
    /// Runtime animation helpers for the Toolkit UI. USS has no keyframes, so the Celeste-style
    /// moves (screen slides, value nudges, the press bounce, the letter-drop title) are driven
    /// here over the element's own scheduler — which also means they auto-pause when the element
    /// leaves the panel, and every timing uses unscaled time because menus run at timeScale 0.
    ///
    /// The signed hover/press states (float up, green highlight, :active press) are pure USS
    /// (Theme.uss); this file only does what USS cannot. Starting a new tween on an element
    /// pauses the previous one, so open/close races resolve to the newer animation.
    /// </summary>
    public static class UiFx
    {
        /// <summary>Celeste OuiOptions: p += DeltaTime * 4, i.e. 0.25s per screen slide.</summary>
        public const float ScreenSlideSeconds = 0.25f;

        // One live tween per element: a second tween on the same element pauses the first.
        private static readonly Dictionary<VisualElement, IVisualElementScheduledItem> Live =
            new Dictionary<VisualElement, IVisualElementScheduledItem>();

        // Static fields do not clear on scene reload; with Domain Reload off, entries from the
        // previous run would linger (same reason as UiBus.ResetStatics).
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => Live.Clear();

        /// <summary>Drives apply(ease(t)) with t in [0,1] over duration seconds (unscaled),
        /// after an optional delay. onComplete runs at most once: on the frame t reaches 1, or
        /// immediately when the element is not on a panel at the call. A newer tween on the same
        /// element cancels this one, and its onComplete never runs.</summary>
        public static void Tween(VisualElement el, float duration, Func<float, float> ease, Action<float> apply,
            float delay = 0f, Action onComplete = null)
        {
            if (el == null) return;

            if (el.panel == null || duration <= 0f)
            {
                if (el.panel != null) apply(ease(1f));
                onComplete?.Invoke();
                return;
            }

            if (Live.TryGetValue(el, out IVisualElementScheduledItem previous))
                previous?.Pause();

            float startTime = Time.unscaledTime + delay;
            IVisualElementScheduledItem item = null;
            item = el.schedule.Execute(() =>
            {
                float t = (Time.unscaledTime - startTime) / duration;
                if (t < 0f) return;

                if (t >= 1f)
                {
                    Live.Remove(el);
                    item.Pause();
                    apply(ease(1f));
                    onComplete?.Invoke();
                    return;
                }

                apply(ease(t));
            });
            item.Every(0);
            Live[el] = item;
        }

        // ---- Screen slides (Celeste Oui* enter/leave) ----

        /// <summary>Slides the panel root in from <paramref name="fromX"/> with CubeOut, fading up.</summary>
        public static void SlideIn(VisualElement el, float fromX, float duration = ScreenSlideSeconds,
            float delay = 0f, Action onComplete = null)
        {
            el.style.translate = new Translate(fromX, 0f, 0f);
            el.style.opacity = 0f;
            Tween(el, duration, Easing.CubeOut, t =>
            {
                el.style.translate = new Translate(Mathf.Lerp(fromX, 0f, t), 0f, 0f);
                el.style.opacity = t;
            }, delay, () =>
            {
                el.style.translate = StyleKeyword.Null;
                el.style.opacity = new StyleFloat(1f);
                onComplete?.Invoke();
            });
        }

        /// <summary>Slides the panel root out to <paramref name="toX"/> with CubeIn (Celeste's leave ease).</summary>
        public static void SlideOut(VisualElement el, float toX, float duration = ScreenSlideSeconds,
            Action onComplete = null)
        {
            Tween(el, duration, Easing.CubeIn, t =>
            {
                el.style.translate = new Translate(Mathf.Lerp(0f, toX, t), 0f, 0f);
                el.style.opacity = 1f - t;
            }, 0f, () =>
            {
                el.style.translate = StyleKeyword.Null;
                el.style.opacity = new StyleFloat(0f);
                onComplete?.Invoke();
            });
        }

        /// <summary>OuiFileSelect: slots arrive with a per-slot offset. Celeste used 0.02s; 0.06s
        /// reads as a cascade at our card size. baseDelay puts the cascade after the sheet's own
        /// slide-in when the cards also ride the sheet (they start one screen further out).</summary>
        public static void SlideInStaggered(IReadOnlyList<VisualElement> items, float fromX,
            float stagger = 0.06f, float duration = ScreenSlideSeconds, float baseDelay = 0f)
        {
            for (int i = 0; i < items.Count; i++)
                SlideIn(items[i], fromX, duration, baseDelay + i * stagger);
        }

        // ---- Press feel (keyboard/gamepad submit never produces :active) ----

        /// <summary>Quick squash then an overshooting return: the "pressed" half of the signed
        /// interaction for the Submit path. Mouse presses already get :active from USS.</summary>
        public static void Bounce(VisualElement el)
        {
            Tween(el, 0.06f, Easing.CubeOut, t => SetUniformScale(el, Mathf.Lerp(1f, 0.94f, t)), 0f, () =>
                Tween(el, 0.14f, Easing.BackOut, t => SetUniformScale(el, Mathf.Lerp(0.94f, 1f, t)), 0f,
                    () => el.style.scale = StyleKeyword.Null));
        }

        /// <summary>Something arrived: a quick swell to 1.18 and an overshooting settle back — the
        /// HUD backpack plate catching a capacity crystal.</summary>
        public static void Pop(VisualElement el)
        {
            Tween(el, 0.08f, Easing.CubeOut, t => SetUniformScale(el, Mathf.Lerp(1f, 1.18f, t)), 0f, () =>
                Tween(el, 0.25f, Easing.BackOut, t => SetUniformScale(el, Mathf.Lerp(1.18f, 1f, t)), 0f,
                    () => el.style.scale = StyleKeyword.Null));
        }

        private static void SetUniformScale(VisualElement el, float s) =>
            el.style.scale = new Scale(new Vector3(s, s, 1f));

        // ---- Option value switch (Celeste Option.Render: ValueWiggler, ±8px cosine decay) ----

        public static void Nudge(VisualElement el, float dir)
        {
            Tween(el, 0.25f, t => t, t =>
            {
                float offset = dir * 8f * Mathf.Cos(t * 3f * 2f * Mathf.PI) * (1f - t);
                el.style.translate = new Translate(offset, 0f, 0f);
            }, 0f, () => el.style.translate = StyleKeyword.Null);
        }

        // ---- End-of-range shake ----

        /// <summary>"No further": a quick decaying shake that first kicks the way the player pushed
        /// (dir -1 = left, +1 = right), ±12px over 0.25s.</summary>
        public static void Shake(VisualElement el, float dir)
        {
            float sign = dir < 0f ? -1f : 1f;
            Tween(el, 0.25f, t => t, t =>
            {
                float offset = sign * 12f * Mathf.Sin(t * 4f * 2f * Mathf.PI) * (1f - t);
                el.style.translate = new Translate(offset, 0f, 0f);
            }, 0f, () => el.style.translate = StyleKeyword.Null);
        }

        // ---- HUD clock tick ----

        /// <summary>A quick decaying rock about the element's transform origin (±10°, 0.3s) — the
        /// HUD clock ticking over a second.</summary>
        public static void Wobble(VisualElement el)
        {
            Tween(el, 0.3f, t => t, t =>
            {
                float angle = 10f * Mathf.Sin(t * 3f * 2f * Mathf.PI) * (1f - t);
                el.style.rotate = new Rotate(Angle.Degrees(angle));
            }, 0f, () => el.style.rotate = StyleKeyword.Null);
        }

        // ---- Letter-drop title (Celeste AreaCompleteTitle) ----

        /// <summary>Builds a one-Label-per-letter title inside container and drops the letters in:
        /// 0.2s head delay plus 0.02s per letter, each falling from -100px to +60px past the line
        /// and settling with a squash flip — the AreaCompleteTitle trajectory, compressed.</summary>
        public static void DropTitle(VisualElement container, string text, float fontSize)
        {
            container.Clear();

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                var letter = new Label(c == ' ' ? string.Empty : c.ToString());
                letter.AddToClassList("header-outline");
                letter.AddToClassList("title-font");
                letter.style.fontSize = fontSize;
                letter.style.marginRight = c == ' ' ? 22f : 6f;
                if (c == ' ') letter.style.width = 22f;
                container.Add(letter);

                float delay = 0.2f + i * 0.02f;

                Tween(letter, 0.18f, Easing.CubeIn, t =>
                {
                    letter.style.translate = new Translate(0f, Mathf.Lerp(-100f, 60f, t), 0f);
                    letter.style.opacity = new StyleFloat(Mathf.Clamp01(t * 3f));
                    SetSquash(letter, Mathf.Lerp(0.75f, 1f, t), Mathf.Lerp(1.5f, 1f, t));
                }, delay, () =>
                {
                    SetSquash(letter, 1.5f, 0.75f);     // the landing flip
                    Tween(letter, 0.24f, Easing.CubeOut, t =>
                    {
                        letter.style.translate = new Translate(0f, Mathf.Lerp(60f, 0f, t), 0f);
                        SetSquash(letter, Mathf.Lerp(1.5f, 1f, t), Mathf.Lerp(0.75f, 1f, t));
                    }, 0f, () => letter.style.translate = StyleKeyword.Null);
                });
            }
        }

        private static void SetSquash(VisualElement el, float x, float y) =>
            el.style.scale = new Scale(new Vector3(x, y, 1f));
    }
}

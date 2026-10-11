using System;
using Inkform.Bus;
using UnityEngine;
using UnityEngine.UIElements;

namespace Inkform.UI
{
    /// <summary>
    /// A settings row with a bar: the label on the left, the slider and its readout on the
    /// right. The row is the focusable control; the Unity Slider inside is not (the mouse still
    /// drags it, and pressing it focuses the row).
    ///
    /// Left / right are held, not stepped: the settings sheet calls Drive every frame with the
    /// horizontal input (left stick past the menu dead zone, d-pad, arrow keys), and the value
    /// moves while it is held — a tap nudges it by 1%, a hold runs at half the range per second
    /// and at the full range per second after HoldBoost, faster the further the stick is pushed.
    /// Reaching an end while still pushing shakes the bar once and raises UiBus.Bumped; it stays
    /// quiet until the push is let go. The navigation events for left / right are swallowed here
    /// so focus never leaves the row.
    /// </summary>
    public class SliderRow : VisualElement
    {
        private const float TapShare = 0.01f;
        private const float SlowShare = 0.5f;     // of the range per second
        private const float FastShare = 1f;
        private const float HoldBoost = 0.6f;     // seconds held before the fast speed

        private readonly Slider slider;
        private readonly Label readout;
        private readonly VisualElement control;
        private readonly Func<float, string> format;

        private int pushDir;
        private float pushStart;
        private bool bumped;

        /// <summary>The value changed by the player (drag, stick, keys) — not by Refresh.</summary>
        public event Action<float> ValueChanged;

        public SliderRow(string labelText, float min, float max, Func<float, string> format)
        {
            this.format = format;

            AddToClassList("settings-row");
            AddToClassList("slider-row");
            AddToClassList("floaty");
            focusable = true;

            var label = new Label(labelText);
            label.AddToClassList("row-label");
            label.AddToClassList("outline");
            label.pickingMode = PickingMode.Ignore;
            Add(label);

            control = new VisualElement { pickingMode = PickingMode.Ignore };
            control.AddToClassList("row-control");
            Add(control);

            slider = new Slider(min, max) { focusable = false };
            slider.AddToClassList("settings-slider");
            control.Add(slider);

            readout = new Label();
            readout.AddToClassList("row-readout");
            readout.AddToClassList("outline");
            readout.pickingMode = PickingMode.Ignore;
            readout.style.display = format != null ? DisplayStyle.Flex : DisplayStyle.None;
            control.Add(readout);

            slider.RegisterValueChangedCallback(e =>
            {
                UpdateReadout(e.newValue);
                ValueChanged?.Invoke(e.newValue);
            });

            RegisterCallback<NavigationMoveEvent>(OnNavigate);
            RegisterCallback<PointerDownEvent>(_ => Focus(), TrickleDown.TrickleDown);
            RegisterCallback<PointerEnterEvent>(_ => UiBus.RaiseHovered());
            RegisterCallback<FocusInEvent>(_ => UiBus.RaiseHovered());
        }

        public float Value => slider.value;

        /// <summary>Shows a stored value (a pull: ValueChanged does not fire).</summary>
        public void SetValueWithoutNotify(float value)
        {
            slider.SetValueWithoutNotify(value);
            UpdateReadout(value);
        }

        /// <summary>Per frame while this row has focus: horizontal input in -1..1, 0 = released.</summary>
        public void Drive(float input, float deltaTime)
        {
            if (Mathf.Abs(input) < 0.001f)
            {
                pushDir = 0;
                return;
            }

            int dir = input > 0f ? 1 : -1;
            float now = Time.unscaledTime;
            float range = slider.highValue - slider.lowValue;
            float delta;
            if (dir != pushDir)
            {
                // A new push: the tap's nudge lands at once
                pushDir = dir;
                pushStart = now;
                bumped = false;
                delta = dir * range * TapShare;
            }
            else
            {
                float share = now - pushStart < HoldBoost ? SlowShare : FastShare;
                delta = dir * range * share * Mathf.Abs(input) * deltaTime;
            }

            float next = Mathf.Clamp(slider.value + delta, slider.lowValue, slider.highValue);
            if (!Mathf.Approximately(next, slider.value)) slider.value = next;

            bool atEnd = dir > 0 ? slider.value >= slider.highValue : slider.value <= slider.lowValue;
            if (atEnd && !bumped)
            {
                bumped = true;
                UiFx.Shake(control, dir);
                UiBus.RaiseBumped();
            }
        }

        private void OnNavigate(NavigationMoveEvent e)
        {
            if (e.direction == NavigationMoveEvent.Direction.Left || e.direction == NavigationMoveEvent.Direction.Right)
                e.StopImmediatePropagation();
        }

        private void UpdateReadout(float value)
        {
            if (format != null) readout.text = format(value);
        }
    }
}

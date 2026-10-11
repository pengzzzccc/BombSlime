using System;
using System.Collections.Generic;
using Inkform.Settings;
using UnityEngine;
using UnityEngine.UIElements;

namespace Inkform.UI
{
    /// <summary>
    /// One section of the single-page settings sheet (AUDIO / VIDEO / CONTROLS). A section builds
    /// its title and rows straight into the sheet's shared row list (#Rows inside the one
    /// ScrollView), in order, so the whole sheet reads top to bottom like the design sketch.
    ///
    /// The row vocabulary lives here so every section builds rows the same way: choice rows and
    /// action rows (OptionRow), bars (SliderRow), group sub-headers. Every row reads and writes
    /// the store through delegates, so Refresh is one pass over the rows — sections only add what
    /// is their own (the controls' device-dependent tables). Row styles live in Settings.uss.
    /// </summary>
    public abstract class SettingsSection
    {
        protected readonly SettingsPanel Owner;
        private readonly VisualElement rows;
        private readonly List<OptionRow> choiceRows = new List<OptionRow>();
        private readonly List<(SliderRow row, Func<float> get)> sliderRows = new List<(SliderRow, Func<float>)>();

        protected UIManager UI => Owner.Ui;

        protected SettingsSection(SettingsPanel owner, VisualElement rows, string title)
        {
            Owner = owner;
            this.rows = rows;

            var header = new Label(title);
            header.AddToClassList("section-title");
            header.AddToClassList("title-font");
            header.AddToClassList("header-outline");
            header.pickingMode = PickingMode.Ignore;
            rows?.Add(header);
        }

        /// <summary>Pull SettingsStore's current values into every row of this section.</summary>
        public virtual void Refresh()
        {
            foreach (OptionRow row in choiceRows) row.Refresh();
            foreach ((SliderRow row, Func<float> get) in sliderRows) row.SetValueWithoutNotify(get());
        }

        /// <summary>Per frame while the sheet is open (unscaled, from the sheet).</summary>
        public virtual void Tick() { }

        // ---- Row vocabulary ----

        protected Label AddSubHeader(string text)
        {
            var header = new Label(text);
            header.AddToClassList("subheader");
            header.AddToClassList("outline");
            header.pickingMode = PickingMode.Ignore;
            rows?.Add(header);
            return header;
        }

        /// <summary>An ordered choice: count, current index, pick, and how each choice reads.</summary>
        protected OptionRow AddChoiceRow(string labelText, Func<int> count, Func<int> get, Action<int> set,
            Func<int, string> text)
        {
            var row = new OptionRow(labelText);
            row.Bind(count, get, set, text);
            rows?.Add(row);
            choiceRows.Add(row);
            return row;
        }

        /// <summary>OFF / ON: left is off, right is on, confirm flips it.</summary>
        protected OptionRow AddOnOffRow(string labelText, Func<bool> get, Action<bool> set) =>
            AddChoiceRow(labelText, () => 2, () => get() ? 1 : 0, i => set(i == 1), i => OnOffText(i == 1));

        /// <summary>A choice over a fixed list of values, matched by value (not index).</summary>
        protected OptionRow AddListRow<T>(string labelText, IReadOnlyList<T> values, Func<T> get, Action<T> set,
            Func<T, string> text)
        {
            return AddChoiceRow(labelText, () => values.Count,
                () => Mathf.Max(0, IndexOf(values, get())),
                i => set(values[i]),
                i => text(values[i]));
        }

        /// <summary>A confirm-only row (no value, no chevrons): Unstuck, Reset Bindings, Reset All.</summary>
        protected OptionRow AddActionRow(string labelText, Action onConfirm)
        {
            var row = new OptionRow(labelText);
            row.AsAction();
            row.Confirmed += onConfirm;
            rows?.Add(row);
            return row;
        }

        /// <summary>A bar: get pulls the stored value, set pushes the player's changes.</summary>
        protected SliderRow AddSliderRow(string labelText, float min, float max, Func<float, string> format,
            Func<float> get, Action<float> set)
        {
            var row = new SliderRow(labelText, min, max, format);
            row.ValueChanged += set;
            rows?.Add(row);
            sliderRows.Add((row, get));
            return row;
        }

        protected VisualElement AddGroup()
        {
            var group = new VisualElement();
            rows?.Add(group);
            return group;
        }

        private static int IndexOf<T>(IReadOnlyList<T> values, T value)
        {
            EqualityComparer<T> eq = EqualityComparer<T>.Default;
            for (int i = 0; i < values.Count; i++)
                if (eq.Equals(values[i], value)) return i;
            return -1;
        }

        // ---- Shared formatting helpers ----

        protected static string Percent(float value) => $"{Mathf.RoundToInt(value * 100f)}%";

        /// <summary>Sensitivity readout. A plain multiplier ("2.5"), not a percentage — the
        /// range runs to 5x and "500%" reads as a much bigger number than it is.</summary>
        protected static string SensText(float value) => value.ToString("0.0");

        protected static string FormatRes(int width, int height) => $"{width} x {height}";

        protected static string FpsText(int value) => value == 0 ? "Uncapped" : value.ToString();

        protected static string OnOffText(bool value) => value ? "ON" : "OFF";

        protected static string DeviceText(SettingsStore.InputDevice device) =>
            device == SettingsStore.InputDevice.KeyboardMouse ? "Keyboard + Mouse" : "Gamepad";

        /// <summary>Intervals are stored in seconds but shown as their reciprocal in Hz
        /// ("10 Hz" .. "0.2 Hz"), matching how the numbers read on the panel.</summary>
        protected static string RateText(float intervalSeconds)
        {
            float hz = 1f / Mathf.Max(intervalSeconds, 0.0001f);
            return $"{hz:0.#} Hz";
        }
    }
}

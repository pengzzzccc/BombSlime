using System;
using Inkform.Bus;
using UnityEngine;
using UnityEngine.UIElements;

namespace Inkform.UI
{
    /// <summary>
    /// One settings row: the label on the left, and on the right either an ordered choice between
    /// a pair of chevrons ("&lt; ON &gt;", "&lt; 1920 x 1080 &gt;") or nothing at all (an action row:
    /// Unstuck, Reset Bindings, Reset All). The row itself is the focusable control.
    ///
    /// A choice row reads and writes its value through the delegates handed to Bind, so the row
    /// never holds a stale copy. Left / right (stick, d-pad, arrow keys) step one choice and stop
    /// at the ends: pushing past the first or last choice shakes the value and raises
    /// UiBus.Bumped (the pad gives a short bump) instead of wrapping around. Held against the
    /// end, the repeats stay quiet until the push is let go. Confirm — a click, pad Submit or
    /// Enter — moves to the next choice and does wrap, a quick way to cycle; a click on a chevron
    /// steps that way.
    ///
    /// Left / right never move focus off the row: StopImmediatePropagation keeps the focus
    /// controller (a propagation-phase handler since Unity 6) from seeing them.
    /// </summary>
    public class OptionRow : VisualElement
    {
        // A blocked push repeated within this window is the same push held: no second shake
        private const float BumpRepeatSeconds = 0.3f;

        private readonly VisualElement control;
        private readonly Label valueLabel;
        private readonly Label leftArrow;
        private readonly Label rightArrow;

        private Func<int> count;
        private Func<int> get;
        private Action<int> set;
        private Func<int, string> text;

        private int bumpDir;
        private float bumpAt = -10f;

        /// <summary>An action row was confirmed (click, Submit, Enter).</summary>
        public event Action Confirmed;

        public OptionRow(string labelText)
        {
            AddToClassList("settings-row");
            AddToClassList("option-row");
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

            leftArrow = Arrow("<", -1);
            control.Add(leftArrow);

            valueLabel = new Label();
            valueLabel.AddToClassList("row-value");
            valueLabel.AddToClassList("outline");
            valueLabel.pickingMode = PickingMode.Ignore;
            control.Add(valueLabel);

            rightArrow = Arrow(">", +1);
            control.Add(rightArrow);

            RegisterCallback<NavigationMoveEvent>(OnNavigate);
            RegisterCallback<NavigationSubmitEvent>(_ => Confirm());
            RegisterCallback<ClickEvent>(_ => Confirm());
            RegisterCallback<PointerEnterEvent>(_ => UiBus.RaiseHovered());
            RegisterCallback<FocusInEvent>(_ => UiBus.RaiseHovered());
        }

        /// <summary>True once Bind gave the row its choices; an unbound row is an action row.</summary>
        public bool IsChoice => count != null;

        /// <summary>Makes this a choice row: how many choices there are, which one is current,
        /// how to pick one, and how each reads.</summary>
        public void Bind(Func<int> count, Func<int> get, Action<int> set, Func<int, string> text)
        {
            this.count = count;
            this.get = get;
            this.set = set;
            this.text = text;
            control.style.display = DisplayStyle.Flex;
            Refresh();
        }

        /// <summary>Turns the row into an action row: no chevrons, no value.</summary>
        public void AsAction() => control.style.display = DisplayStyle.None;

        /// <summary>Pulls the current choice from the store (raises nothing: re-opening the sheet
        /// stays silent).</summary>
        public void Refresh()
        {
            if (!IsChoice) return;
            int n = Mathf.Max(1, count());
            int i = Mathf.Clamp(get(), 0, n - 1);
            valueLabel.text = text(i);
            leftArrow.EnableInClassList("row-arrow--end", i <= 0);
            rightArrow.EnableInClassList("row-arrow--end", i >= n - 1);
        }

        // ---- Input ----

        private void OnNavigate(NavigationMoveEvent e)
        {
            switch (e.direction)
            {
                case NavigationMoveEvent.Direction.Left:
                    e.StopImmediatePropagation();
                    if (IsChoice) Step(-1);
                    break;
                case NavigationMoveEvent.Direction.Right:
                    e.StopImmediatePropagation();
                    if (IsChoice) Step(+1);
                    break;
            }
        }

        private void Step(int dir)
        {
            int n = count();
            int i = get();
            if (!CanStep(i, dir, n))
            {
                Bump(dir);
                return;
            }

            set(i + dir);
            Refresh();
            UiFx.Nudge(valueLabel, dir);
            UiBus.RaiseToggled(dir > 0);
        }

        private void Confirm()
        {
            if (!enabledInHierarchy) return;
            if (!IsChoice)
            {
                UiFx.Bounce(this);
                UiBus.RaiseClicked();
                Confirmed?.Invoke();
                return;
            }

            set(Wrap(get() + 1, count()));
            Refresh();
            UiFx.Nudge(valueLabel, +1);
            UiBus.RaiseToggled(true);
        }

        private void Bump(int dir)
        {
            float now = Time.unscaledTime;
            bool held = dir == bumpDir && now - bumpAt < BumpRepeatSeconds;
            bumpDir = dir;
            bumpAt = now;
            if (held) return;

            UiFx.Shake(control, dir);
            UiBus.RaiseBumped();
        }

        private Label Arrow(string glyph, int dir)
        {
            var arrow = new Label(glyph);
            arrow.AddToClassList("row-arrow");
            arrow.AddToClassList("outline");
            // A chevron click steps that way (and bumps at the end) rather than confirming
            arrow.RegisterCallback<ClickEvent>(e =>
            {
                e.StopPropagation();
                if (!enabledInHierarchy || !IsChoice) return;
                Focus();
                Step(dir);
            });
            return arrow;
        }

        // ---- Pure helpers (tested) ----

        /// <summary>A step stays inside the choices: no wrap-around at either end.</summary>
        public static bool CanStep(int index, int dir, int count)
        {
            int next = index + dir;
            return next >= 0 && next < count;
        }

        /// <summary>Confirm cycles: past the last choice comes the first.</summary>
        public static int Wrap(int index, int count)
        {
            if (count <= 0) return 0;
            int r = index % count;
            return r < 0 ? r + count : r;
        }
    }
}

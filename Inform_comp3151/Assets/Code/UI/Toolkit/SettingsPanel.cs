using System.Collections.Generic;
using Inkform.Input;
using Inkform.Settings;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace Inkform.UI
{
    /// <summary>
    /// Settings sheet, one page: the AUDIO, VIDEO and CONTROLS sections stacked in a single
    /// ScrollView, each built by its SettingsSection into the shared #Rows list.
    ///
    /// Pad handling, on top of the focus navigation every sheet shares:
    ///  - Left stick (UiStick, menu dead zone) / d-pad / arrow keys pick a row; left / right
    ///    change its value on the spot (OptionRow steps, SliderRow is driven from Tick while the
    ///    push is held).
    ///  - Right stick scrolls the page. The focused row is kept on screen when the selection
    ///    moves; after the page was scrolled away from it, the next up / down picks the nearest
    ///    row on screen instead of walking from the one out of sight.
    ///
    /// Esc / pad B / Start close the sheet (UIManager's escape stack → CloseSettings). Opening it
    /// starts at the top with every row re-read from the store.
    /// </summary>
    public class SettingsPanel : ToolkitPanel
    {
        private const float ScrollDeadZone = 0.2f;
        private const float ScrollSpeed = 1800f;        // px per second at full deflection
        private const float KeepVisibleMargin = 100f;   // px between the focused row and the viewport edge
        private const float FollowSeconds = 0.15f;

        private readonly ScrollView scroll;
        private readonly VisualElement rows;
        private readonly SettingsSection[] sections;

        // Follow-the-focus scroll, run from Tick so the right stick can take over mid-way
        private float followFrom, followTo, followElapsed = -1f;

        /// <summary>The UIManager handle the sections need (Unstuck etc.) — the ToolkitPanel's
        /// own UI is protected.</summary>
        internal UIManager Ui => UI;

        public SettingsPanel(VisualElement root, UIManager ui) : base(root, ui)
        {
            scroll = Q<ScrollView>("Scroll");
            rows = Q("Rows");

            sections = new SettingsSection[]
            {
                new SettingsAudioSection(this, rows),
                new SettingsVideoSection(this, rows),
                new SettingsControlsSection(this, rows),
            };

            rows?.RegisterCallback<FocusInEvent>(OnRowFocused, TrickleDown.TrickleDown);
            Root.RegisterCallback<NavigationMoveEvent>(OnNavigateFromOffscreen, TrickleDown.TrickleDown);
        }

        protected override void DefinePrompts(PromptBar bar) => bar
            .Add(PromptBar.Key.Select, "Select")
            .Add(PromptBar.Key.Change, "Change")
            .Add(PromptBar.Key.Scroll, "Scroll")
            .Add(PromptBar.Key.Confirm, "Confirm")
            .Add(PromptBar.Key.Back, "Back", UI.CloseSettings);

        /// <summary>RESET ALL: store defaults + the shared binding reset, then every row re-reads
        /// the store. ResetToDefaults already deletes the persisted binding JSON; the shared
        /// asset's in-memory overrides must go too, or the rows would show custom keys while the
        /// save is empty.</summary>
        internal void ResetAll()
        {
            SettingsStore.ResetToDefaults();
            BindingTools.ResetAllBindings();
            RefreshAll();
        }

        private void RefreshAll()
        {
            foreach (SettingsSection section in sections) section.Refresh();
        }

        // ---- Lifecycle ----

        protected override void OnOpen()
        {
            RefreshAll();
            followElapsed = -1f;
            if (scroll != null) scroll.scrollOffset = Vector2.zero;
        }

        protected override void OnClose()
        {
            // A half-finished interactive rebind must not keep swallowing input after close
            BindingTools.CancelActive();
        }

        protected internal override void Tick(float unscaledDelta)
        {
            if (!IsOpen) return;
            foreach (SettingsSection section in sections) section.Tick();

            ScrollWithRightStick(unscaledDelta);
            RunFollow(unscaledDelta);

            if (FocusedElement() is SliderRow slider) slider.Drive(HorizontalHold(), unscaledDelta);
        }

        // ---- Scrolling ----

        private void ScrollWithRightStick(float dt)
        {
            Gamepad pad = Gamepad.current;
            if (pad == null || scroll == null) return;

            float y = pad.rightStick.ReadValue().y;
            float magnitude = Mathf.Abs(y);
            if (magnitude < ScrollDeadZone) return;

            float t = Mathf.InverseLerp(ScrollDeadZone, 1f, magnitude);
            followElapsed = -1f;   // the player took over the scroll
            // Stick up = toward the top of the page
            ScrollTo(scroll.scrollOffset.y - Mathf.Sign(y) * ScrollSpeed * t * t * dt);
        }

        // The focused row stays clear of the viewport edges: a short ease to the nearest offset
        // that shows it with the margin
        private void OnRowFocused(FocusInEvent e)
        {
            if (scroll == null || !(e.target is VisualElement row)) return;

            float viewport = scroll.contentViewport.layout.height;
            if (viewport <= 0f) return;
            float top = row.worldBound.yMin - scroll.contentContainer.worldBound.yMin;
            float bottom = top + row.worldBound.height;
            float offset = scroll.scrollOffset.y;

            float target = offset;
            if (top - KeepVisibleMargin < offset) target = top - KeepVisibleMargin;
            else if (bottom + KeepVisibleMargin > offset + viewport) target = bottom + KeepVisibleMargin - viewport;
            if (Mathf.Approximately(target, offset)) return;

            followFrom = offset;
            followTo = target;
            followElapsed = 0f;
        }

        private void RunFollow(float dt)
        {
            if (followElapsed < 0f || scroll == null) return;
            followElapsed += dt;
            float t = Mathf.Clamp01(followElapsed / FollowSeconds);
            ScrollTo(Mathf.Lerp(followFrom, followTo, Easing.CubeOut(t)));
            if (t >= 1f) followElapsed = -1f;
        }

        private void ScrollTo(float y)
        {
            float max = scroll.verticalScroller != null ? scroll.verticalScroller.highValue : 0f;
            scroll.scrollOffset = new Vector2(0f, Mathf.Clamp(y, 0f, Mathf.Max(0f, max)));
        }

        // The page was scrolled away from the focused row: up / down start from what is on
        // screen — the topmost visible row going down, the lowest going up
        private void OnNavigateFromOffscreen(NavigationMoveEvent e)
        {
            if (scroll == null) return;
            bool down = e.direction == NavigationMoveEvent.Direction.Down;
            bool up = e.direction == NavigationMoveEvent.Direction.Up;
            if (!down && !up) return;

            VisualElement focused = FocusedElement();
            Rect view = scroll.contentViewport.worldBound;
            if (focused == null || !rows.Contains(focused) || Overlaps(focused.worldBound, view)) return;

            VisualElement pick = null;
            foreach (VisualElement row in SelectableRows())
            {
                Rect r = row.worldBound;
                if (r.yMin < view.yMin || r.yMax > view.yMax) continue;
                if (down) { pick = row; break; }
                pick = row;   // going up: keep the last one fully on screen
            }
            if (pick == null) return;

            e.StopImmediatePropagation();
            pick.Focus();
        }

        private IEnumerable<VisualElement> SelectableRows()
        {
            foreach (VisualElement el in rows.Query(className: "floaty").Build())
            {
                if (el.focusable && el.enabledInHierarchy && el.resolvedStyle.display != DisplayStyle.None
                    && el.parent != null && el.parent.resolvedStyle.display != DisplayStyle.None)
                    yield return el;
            }
        }

        private static bool Overlaps(Rect a, Rect b) => a.yMax > b.yMin && a.yMin < b.yMax;

        // ---- Bars ----

        /// <summary>Held horizontal input for a bar: the left stick past the menu dead zone, else
        /// the d-pad, else the arrow / A D keys.</summary>
        private static float HorizontalHold()
        {
            float stick = UiStick.ReadLeft().x;
            if (Mathf.Abs(stick) > 0f) return stick;

            Gamepad pad = Gamepad.current;
            if (pad != null)
            {
                float dpad = pad.dpad.ReadValue().x;
                if (Mathf.Abs(dpad) > 0.5f) return Mathf.Sign(dpad);
            }

            Keyboard kb = Keyboard.current;
            if (kb == null) return 0f;
            float keys = 0f;
            if (kb.rightArrowKey.isPressed || kb.dKey.isPressed) keys += 1f;
            if (kb.leftArrowKey.isPressed || kb.aKey.isPressed) keys -= 1f;
            return keys;
        }

        private VisualElement FocusedElement() =>
            Root.panel?.focusController.focusedElement is VisualElement focused && Root.Contains(focused) ? focused : null;
    }
}

using System;
using Inkform.Bus;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.UIElements;

namespace Inkform.UI
{
    /// <summary>
    /// UI Toolkit replacement for the old uGUI BasePanel. A panel is one full-screen sheet built
    /// from a UXML template (instantiated once by the UIManager at startup, hidden by display
    /// none); concrete panels find their controls by element name (Q), exactly the naming
    /// convention BasePanel.FindButton used.
    ///
    /// Panels are plain C# objects, not MonoBehaviours — they are created and kept alive by the
    /// UIManager and outlive scene switches the same way the old prefabs did. Open/Close toggle
    /// visibility and play the Celeste screen slide; per-frame logic hooks Tick (driven from
    /// UIManager.Update, unscaled like every menu timer).
    /// </summary>
    public abstract class ToolkitPanel
    {
        protected readonly VisualElement Root;
        protected readonly UIManager UI;

        public bool IsOpen { get; private set; }

        /// <summary>Real time the sheet last entered the open state. Buttons swallow clicks for
        /// a short grace window after it: the freshly unlocked cursor sits dead-centre on the
        /// pause menu's RESUME row, and a stray gameplay click / double-Esc / Submit echo must
        /// not instantly execute whatever it lands on (Celeste does the same on its screens).</summary>
        protected float OpenedAtRealtime { get; private set; } = -10f;

        private const float InputGraceSeconds = 0.3f;
        private bool InInputGrace => Time.unscaledTime - OpenedAtRealtime < InputGraceSeconds;

        /// <summary>Where this sheet enters from, in theme pixels. Every sheet enters from the
        /// right screen edge and leaves to the left one — single-panel navigation pushes the
        /// outgoing sheet off left while the incoming one slides in from the right.</summary>
        protected virtual float EnterFromX => 1920f;

        /// <summary>Sheets always leave to the left, whichever side they came in from.</summary>
        protected float ExitToX => -EnterFromX;

        /// <summary>The sheet's bottom-right prompt row (#PromptBar in its UXML), or null when the
        /// sheet has none. Filled by DefinePrompts on the first open.</summary>
        protected PromptBar Prompts { get; }
        private bool promptsDefined;

        protected ToolkitPanel(VisualElement root, UIManager ui)
        {
            Root = root;
            UI = ui;
            Root.style.display = DisplayStyle.None;

            VisualElement promptHost = root.Q("PromptBar");
            if (promptHost != null) Prompts = new PromptBar(promptHost);
        }

        public void Open()
        {
            // Refreshed before the guard on purpose: re-opening an already-open sheet (settings
            // returning to pause) restarts the grace window too.
            OpenedAtRealtime = Time.unscaledTime;
            if (IsOpen) return;

            IsOpen = true;
            // Deferred to the first open: the subclass constructor has finished by then, so its
            // prompts may point at anything it set up
            if (!promptsDefined && Prompts != null)
            {
                promptsDefined = true;
                DefinePrompts(Prompts);
            }
            Root.style.display = DisplayStyle.Flex;
            Root.BringToFront();
            OnOpen();
            PlayEnter();
            FocusFirst();
        }

        public void Close()
        {
            if (!IsOpen) return;

            IsOpen = false;
            OnClose();
            ReleaseFocus();
            PlayLeave(() =>
            {
                // A quick close-open cycle (CloseSettings reopens pause) must not hide the sheet
                // that was opened in between — the guard reads the live state, not the call's.
                if (!IsOpen) Root.style.display = DisplayStyle.None;
            });
        }

        /// <summary>Per-frame tick with unscaled delta; only called while the UI layer lives.</summary>
        protected internal virtual void Tick(float unscaledDelta) { }

        /// <summary>Called once when the whole UI layer dies (UIManager being destroyed): the
        /// spot where a MonoBehaviour's OnDestroy used to release static-event subscriptions and
        /// scoped locks that must not outlive their owner.</summary>
        protected internal virtual void Teardown() { }

        /// <summary>Declares the sheet's prompt row ("Confirm", "Back"...), once, on first open.
        /// Give Back the same action Esc / pad B takes in UIManager, so a click matches the key.</summary>
        protected virtual void DefinePrompts(PromptBar bar) { }

        /// <summary>Hook after the panel becomes visible (before the slide-in starts).</summary>
        protected virtual void OnOpen() { }

        /// <summary>Hook before the panel starts sliding out.</summary>
        protected virtual void OnClose() { }

        protected virtual void PlayEnter() => UiFx.SlideIn(Root, EnterFromX);

        protected virtual void PlayLeave(Action onHidden) => UiFx.SlideOut(Root, ExitToX, UiFx.ScreenSlideSeconds, onHidden);

        // ---- Child lookup, shared by every panel ----

        protected VisualElement Q(string name) => Root.Q(name);

        protected T Q<T>(string name) where T : VisualElement => Root.Q<T>(name);

        /// <summary>
        /// Wires a Button the way BasePanel.Bind did: warn instead of throw when the named
        /// control is missing (one renamed element must not take down the rest of Awake), and
        /// route activation through UiBus so hover/click cues keep working without any panel
        /// knowing the audio system exists. The Bounce is the press feel for the Submit path —
        /// mouse presses already show :active from USS.
        /// </summary>
        protected void Bind(Button button, string name, Action onClick)
        {
            if (button == null)
            {
                Debug.LogWarning($"{GetType().Name}: no element named '{name}' in the sheet — that control does nothing");
                return;
            }

            button.clicked += () =>
            {
                if (InInputGrace) return;   // swallow clicks landing on the fresh sheet

                UiBus.RaiseClicked();
                UiFx.Bounce(button);
                onClick();
            };
            button.RegisterCallback<PointerEnterEvent>(_ => UiBus.RaiseHovered());
            button.RegisterCallback<FocusInEvent>(_ => UiBus.RaiseHovered());
        }

        /// <summary>Focuses the sheet's first live .floaty control, standing in for the old
        /// SelectFirstControl: gamepad/keyboard navigation needs one focused element to start
        /// from, or the highlight is nowhere until the mouse moves.</summary>
        protected void FocusFirst()
        {
            foreach (VisualElement el in Root.Query(className: "floaty").Build())
            {
                if (!el.focusable || !el.enabledSelf) continue;
                if (!VisibleInHierarchy(el)) continue;
                el.Focus();
                return;
            }
        }

        /// <summary>The stick moved with nothing focused on this sheet: start from its first control.</summary>
        internal void Refocus() => FocusFirst();

        private void ReleaseFocus()
        {
            if (Root.panel == null) return;
            if (Root.panel.focusController.focusedElement is VisualElement focused && Root.Contains(focused))
                focused.Blur();
        }

        // ---- Any-key gates (boot cards, the title screen) ----

        /// <summary>Any key, mouse click or pad button pressed this frame.</summary>
        protected static bool AnyInputPressed()
        {
            Keyboard kb = Keyboard.current;
            if (kb != null && kb.anyKey.wasPressedThisFrame) return true;

            Gamepad pad = Gamepad.current;
            if (pad != null && AnyButtonPressed(pad)) return true;

            Mouse mouse = Mouse.current;
            if (mouse != null && (mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame)) return true;
            return false;
        }

        /// <summary>Gamepad exposes no anyButton property: walk the pad's controls and ask each
        /// button directly. Stick axes are AxisControls and skip themselves via the type filter.</summary>
        private static bool AnyButtonPressed(Gamepad pad)
        {
            foreach (InputControl control in pad.allControls)
            {
                if (control is ButtonControl button && button.wasPressedThisFrame) return true;
            }
            return false;
        }

        /// <summary>Inline display-none on any ancestor (e.g. the hidden device group) makes an
        /// element unfocusable in practice; inline styles read immediately, unlike resolvedStyle.</summary>
        private static bool VisibleInHierarchy(VisualElement el)
        {
            for (VisualElement e = el; e != null; e = e.parent)
                if (e.style.display == DisplayStyle.None) return false;
            return true;
        }
    }
}

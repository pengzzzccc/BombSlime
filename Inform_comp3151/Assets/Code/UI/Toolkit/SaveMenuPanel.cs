using System;
using System.Collections.Generic;
using System.Globalization;
using Inkform.Bus;
using Inkform.Save;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace Inkform.UI
{
    /// <summary>
    /// Save menu sheet, Celeste OuiFileSelect styling: three postcard slots that slide in from
    /// the right with a small cascade, ink text on the saveslot plates. Behaviour: a tap continues
    /// (an empty slot starts a new one); the overwrite offer is a one-second hold on the
    /// focused slot — Ctrl (keyboard), the pad's North button, or the card itself with the
    /// mouse — shown as a green fill creeping across the card; a tap then confirms.
    ///
    /// Keyboard/gamepad have no long-press of their own, so the hold key is polled here
    /// (ctrlKey / buttonNorth, same stance as the pause keys in UIManager — no action asset
    /// coupling). The release that ends a completed hold is swallowed by the slot's click
    /// handler, which reads the live hold state (see the note there).
    ///
    /// Captions stay ASCII: the game font (Alibaba PuHuiTi, see UiFonts) covers CJK too, but its
    /// fallbacks (LiberationSans, BombSlimeFonts) only guarantee Latin glyphs.
    /// </summary>
    public class SaveMenuPanel : ToolkitPanel
    {
        // Long enough that no ordinary click reaches it, short enough to find by accident.
        private const float HoldSeconds = 1f;

        // How long an offered overwrite stands before the row goes back to normal. A confirm
        // state that waited forever would be a trap for whoever opens this sheet again later.
        private const float ConfirmSeconds = 3f;

        private readonly Button[] slots;
        private readonly Label[] slotTitles;
        private readonly Label[] slotLevels;
        private readonly VisualElement[] slotStats;   // [clock] time  [skull] deaths
        private readonly Label[] slotTimes;
        private readonly Label[] slotDeaths;
        private readonly Label[] slotNotes;           // save date, or the confirm prompt
        private readonly VisualElement[] holdFills;

        private int confirmSlot = -1;     // the slot currently offering to be overwritten; -1 = none
        private float confirmSeconds;

        private int heldSlot = -1;        // the slot under an unbroken press; -1 = none
        private float heldSeconds;
        private bool holdFired;
        private bool holdKeyWasDown;      // Ctrl / North edge detector (the key-hold is the Tick's job)

        private int focusedSlot = -1;     // the slot holding UI focus — the key-hold's target

        public SaveMenuPanel(VisualElement root, UIManager ui) : base(root, ui)
        {
            int count = SaveStore.SlotCount;
            slots = new Button[count];
            slotTitles = new Label[count];
            slotLevels = new Label[count];
            slotStats = new VisualElement[count];
            slotTimes = new Label[count];
            slotDeaths = new Label[count];
            slotNotes = new Label[count];
            holdFills = new VisualElement[count];

            for (int i = 0; i < count; i++)
            {
                int slot = i;   // captured per iteration: one shared loop variable would bind every row to the last slot

                slots[i] = Q<Button>($"Slot{i}");
                slotTitles[i] = Q<Label>($"SlotTitle{i}");
                slotLevels[i] = Q<Label>($"SlotLevel{i}");
                slotStats[i] = Q<VisualElement>($"SlotStats{i}");
                slotTimes[i] = Q<Label>($"SlotTime{i}");
                slotDeaths[i] = Q<Label>($"SlotDeaths{i}");
                slotNotes[i] = Q<Label>($"SlotNote{i}");
                holdFills[i] = Q<VisualElement>($"HoldFill{i}");

                if (slots[i] == null)
                {
                    Debug.LogWarning($"SaveMenuPanel: no element named 'Slot{i}' in the sheet — that slot does nothing");
                    continue;
                }

                slots[i].clicked += () =>
                {
                    // Callback order trap: Button's Clickable registered its PointerUp handler
                    // before ours, so this click fires BEFORE EndHold resets the hold state.
                    // Read the live hold state here — the release that ends a completed hold
                    // must be swallowed, not read as a tap that continues the run.
                    if (holdFired && heldSlot == slot) return;

                    UiBus.RaiseClicked();
                    UiFx.Bounce(slots[slot]);
                    OnSlotPressed(slot);
                };
                slots[i].RegisterCallback<PointerEnterEvent>(_ => UiBus.RaiseHovered());
                slots[i].RegisterCallback<FocusInEvent>(_ =>
                {
                    focusedSlot = slot;
                    UiBus.RaiseHovered();
                });
                slots[i].RegisterCallback<FocusOutEvent>(_ => { if (focusedSlot == slot) focusedSlot = -1; });
                slots[i].RegisterCallback<PointerDownEvent>(_ => BeginHold(slot));
                slots[i].RegisterCallback<PointerUpEvent>(_ => EndHold());
            }

        }

        // Back = Esc / pad B (UIManager.BackFromSaveMenu); the hold prompt names the key the
        // Tick polls (Ctrl / pad North)
        protected override void DefinePrompts(PromptBar bar) => bar
            .Add(PromptBar.Key.Confirm, "Continue")
            .Add(PromptBar.Key.Overwrite, "Hold to overwrite")
            .Add(PromptBar.Key.Back, "Back", UI.BackFromSaveMenu);

        protected override void PlayEnter()
        {
            // The sheet slides in like every other; the cards ride along but start one screen
            // further out and land just after it, so the three of them cascade into place.
            base.PlayEnter();

            var cards = new List<VisualElement>();
            foreach (Button slot in slots)
                if (slot != null) cards.Add(slot);
            UiFx.SlideInStaggered(cards, EnterFromX, baseDelay: UiFx.ScreenSlideSeconds * 0.4f);
        }

        // Rows are painted on open rather than in the constructor: a run played and quit since
        // the last time this sheet was shown has to read as changed. Changed covers the rarer
        // case of a slot being written while the sheet is up, which nothing does today but a
        // paging arrow one day might.
        protected override void OnOpen()
        {
            ClearConfirm();
            ResetHold();
            SaveStore.Changed += Refresh;
            Refresh();
        }

        protected override void OnClose()
        {
            SaveStore.Changed -= Refresh;
            ClearConfirm();
            ResetHold();
            focusedSlot = -1;
        }

        protected internal override void Teardown() => SaveStore.Changed -= Refresh;

        protected internal override void Tick(float unscaledDelta)
        {
            // The overwrite hold's key edge: Ctrl (keyboard) or North (gamepad) held on the
            // focused slot starts the hold, releasing it ends it. Polled rather than routed
            // through actions — same stance as the pause keys in UIManager.
            bool holdKeyDown = HoldKeyDown();
            if (holdKeyDown && !holdKeyWasDown && focusedSlot >= 0) BeginHold(focusedSlot);
            if (!holdKeyDown && holdKeyWasDown) EndHold();
            holdKeyWasDown = holdKeyDown;

            // Advance the overwrite-hold fill unscaled (this sheet can be reached while
            // timeScale is 0 — the pause menu route into it).
            if (heldSlot >= 0 && !holdFired)
            {
                heldSeconds += unscaledDelta;
                float fill = Mathf.Clamp01(heldSeconds / HoldSeconds);
                SetHoldFill(heldSlot, fill);
                if (fill >= 1f)
                {
                    holdFired = true;
                    OnSlotHeld(heldSlot);
                }
            }

            if (confirmSlot < 0) return;

            confirmSeconds -= unscaledDelta;
            if (confirmSeconds > 0f) return;

            ClearConfirm();
            Refresh();
        }

        private static bool HoldKeyDown()
        {
            // ctrlKey is the Keyboard device's synthetic "either Ctrl" control.
            return (Keyboard.current != null && Keyboard.current.ctrlKey.isPressed)
                || (Gamepad.current != null && Gamepad.current.buttonNorth.isPressed);
        }

        // ---- Slot actions ----

        private void BeginHold(int slot)
        {
            // An empty slot has nothing to overwrite — the hold simply does not start on it.
            if (!SaveStore.HasSave(slot)) return;

            heldSlot = slot;
            heldSeconds = 0f;
            holdFired = false;
            SetHoldFill(slot, 0f);
        }

        private void EndHold()
        {
            int slot = heldSlot;
            heldSlot = -1;
            heldSeconds = 0f;
            holdFired = false;
            SetHoldFill(slot, 0f);
        }

        private void ResetHold()
        {
            heldSlot = -1;
            heldSeconds = 0f;
            holdFired = false;
            for (int i = 0; i < holdFills.Length; i++)
                SetHoldFill(i, 0f);
        }

        private void SetHoldFill(int slot, float fraction)
        {
            if (slot < 0 || slot >= holdFills.Length || holdFills[slot] == null) return;
            holdFills[slot].style.width = new Length(fraction * 100f, LengthUnit.Percent);
        }

        private void OnSlotPressed(int slot)
        {
            // Standing offer to overwrite this row: this tap is the confirmation.
            if (confirmSlot == slot)
            {
                ClearConfirm();
                UI.StartNewGame(slot);
                return;
            }

            // A tap anywhere else drops an offer made on another row rather than leaving two
            // rows in odd states at once.
            ClearConfirm();

            if (SaveStore.HasSave(slot)) UI.ContinueGame(slot);
            else UI.StartNewGame(slot);
        }

        private void OnSlotHeld(int slot)
        {
            // An empty slot already starts a new game on a plain tap; there is nothing to confirm.
            if (!SaveStore.HasSave(slot)) return;

            confirmSlot = slot;
            confirmSeconds = ConfirmSeconds;
            Refresh();      // the row changes while the finger is still down, so letting go reads as safe
        }

        private void ClearConfirm()
        {
            confirmSlot = -1;
            confirmSeconds = 0f;
        }

        // ---- Captions ----

        private void Refresh()
        {
            for (int i = 0; i < slots.Length; i++)
            {
                Caption caption = CaptionFor(i);
                if (slotTitles[i] != null) slotTitles[i].text = caption.slot;
                if (slotLevels[i] != null) slotLevels[i].text = caption.level;
                if (slotStats[i] != null) slotStats[i].style.display = caption.time != null ? DisplayStyle.Flex : DisplayStyle.None;
                if (slotTimes[i] != null) slotTimes[i].text = caption.time ?? string.Empty;
                if (slotDeaths[i] != null) slotDeaths[i].text = caption.deaths ?? string.Empty;
                if (slotNotes[i] != null) slotNotes[i].text = caption.note;
            }
        }

        /// <summary>One card's text: slot number and level on the left; on the plate's line at the
        /// right, the run's time and deaths (beside the clock and skull icons — null hides that
        /// row) over a note (the save date, or the confirm prompt).</summary>
        private struct Caption
        {
            public string slot;
            public string level;
            public string time;
            public string deaths;
            public string note;
        }

        private Caption CaptionFor(int slot)
        {
            var caption = new Caption { slot = $"SLOT {slot + 1}", note = string.Empty };

            if (confirmSlot == slot)
            {
                caption.level = "Overwrite with a new game?";
                caption.note = "Tap again to confirm";
                return caption;
            }

            SaveData data = SaveStore.Get(slot);
            if (data.IsEmpty)
            {
                caption.level = "New Game";
                return caption;
            }

            caption.level = UIManager.Instance != null
                ? UIManager.Instance.LevelDisplayName(data.sceneName)
                : data.sceneName;
            caption.time = RunTimeFormat.Format(data.playSeconds);
            caption.deaths = data.deaths.ToString();
            caption.note = FormatSavedAt(data.savedAtUtc);
            return caption;
        }

        // Stored as a round-trip UTC string (JsonUtility cannot serialize DateTime); shown in
        // the player's own time zone. An unparseable value is a hand-edited or truncated file —
        // say so quietly rather than throwing on the way to drawing a menu.
        private static string FormatSavedAt(string utcIso)
        {
            if (string.IsNullOrEmpty(utcIso)) return "unknown";

            if (!DateTime.TryParse(utcIso, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out DateTime saved))
                return "unknown";

            return saved.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }
    }
}

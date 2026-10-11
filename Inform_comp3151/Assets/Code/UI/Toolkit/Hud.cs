using Inkform.Bus;
using Inkform.Item;
using Inkform.Level;
using Inkform.Save;
using Inkform.Settings;
using UnityEngine;
using UnityEngine.UIElements;

namespace Inkform.UI
{
    /// <summary>
    /// The gameplay HUD, rebuilt on the Toolkit tree from the old prefab's components: the run
    /// stats (top left: the run timer beside its clock, the death count beside its skull),
    /// FpsDisplay (top right), InventoryHud (bottom right), SaveIndicator (the "Saved" toast above
    /// it) and the HudRoot visibility switch — plus the tutorial hint line (bottom centre,
    /// TutorialHintView). Everything is picking-mode Ignore in the UXML — the HUD never eats a
    /// click meant for the world.
    ///
    /// A plain object driven from UIManager.Update. The run has one clock, SaveStore.PlaySeconds
    /// — the same number the save slot stores and the end sheet reports — and the HUD is what
    /// advances it: every frame the player is in play (in a level, state Playing, no scene
    /// transition), on unscaled time so hitstop counts and pause does not. The timer reads
    /// minutes:seconds:milliseconds; Show Timer only hides it. The toast and FPS counter run on
    /// unscaled time too (hitstop must not freeze them).
    ///
    /// A capacity crystal flies into the backpack plate before its slot counts (CapacityUpgradeFlight):
    /// from its Rise to its Land the readout keeps the old capacity, then the plate pops, the count
    /// flashes green and "+1" floats up off the plate.
    /// </summary>
    public sealed class Hud
    {
        private const float ToastHoldSeconds = 1f;
        private const float ToastFadeSeconds = 0.4f;
        private const float FpsUpdateInterval = 0.5f;   // refresh the label text at most this often
        private const float FpsSmoothing = 0.1f;        // exponential smoothing on the instant fps

        private readonly VisualElement gameplay;
        private readonly VisualElement timerRow;
        private readonly VisualElement clockIcon;
        private readonly Label timerLabel;
        private readonly VisualElement deathIcon;
        private readonly Label deathLabel;
        private readonly Label fpsLabel;
        private readonly VisualElement inventoryRoot;
        private readonly VisualElement inventoryIcon;
        private readonly Label inventoryCount;
        private readonly Label inventoryPlus;
        private readonly Label saveToast;
        private readonly TutorialHintView tutorialHint;

        private bool timerRunning;
        private long shownWholeSecond = -1;   // the clock rocks as each new second comes up
        private int shownDeaths = -1;         // the skull pops as the count goes up
        private int heldCapacity;             // capacity still flying in to the plate: shown when it lands

        private float toastRemaining;

        private float smoothedFps;
        private float fpsElapsed;

        public Hud(VisualElement root)
        {
            gameplay = root.Q("Gameplay");
            timerRow = root.Q("TimerRow");
            clockIcon = root.Q("ClockIcon");
            timerLabel = root.Q<Label>("TimerLabel");
            deathIcon = root.Q("DeathIcon");
            deathLabel = root.Q<Label>("DeathLabel");
            fpsLabel = root.Q<Label>("FpsLabel");
            inventoryRoot = root.Q("Inventory");
            inventoryIcon = root.Q("InventoryIcon");
            inventoryCount = root.Q<Label>("InventoryCount");
            inventoryPlus = root.Q<Label>("InventoryPlus");
            saveToast = root.Q<Label>("SaveToast");
            tutorialHint = new TutorialHintView(root);

            RefreshRunStats();

            InventoryStore.Changed += RefreshInventory;
            ItemBus.CapacityFlight += OnCapacityFlight;
            SettingsStore.Changed += ApplyVisibility;
            SaveStore.Saved += ShowToast;

            RefreshInventory();
            ApplyVisibility();
        }

        // ---- Visibility (old HudRoot) ----

        public void SetGameplayVisible(bool visible)
        {
            if (gameplay != null) gameplay.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;

            timerRunning = visible;
        }

        // ---- Per-frame (old GameTimer / FpsDisplay / SaveIndicator Update loops) ----

        public void Tick(float unscaledDelta)
        {
            // The run's clock advances only while the player can actively play (old GameTimer's
            // rule), on unscaled time: hitstop is play, the pause menu is not
            if (timerRunning
                && GameStateStore.Current == GameStateStore.GameState.Playing
                && !(SceneDirector.Instance != null && SceneDirector.Instance.IsTransitioning))
            {
                SaveStore.AdvancePlayTime(unscaledDelta);
            }
            RefreshRunStats();

            // "Saved" toast: hold, then fade — unscaled, so Save & Quit's final save flashes the
            // same as any other even while the pause menu holds timeScale at 0 (old SaveIndicator).
            if (toastRemaining > 0f && saveToast != null)
            {
                toastRemaining = Mathf.Max(0f, toastRemaining - unscaledDelta);
                saveToast.style.opacity = new StyleFloat(Mathf.Clamp01(toastRemaining / ToastFadeSeconds));
            }

            // Tutorial hint fades on unscaled time too: hitstop must not stall it mid-fade
            tutorialHint.Tick(unscaledDelta);

            // FPS readout: unscaled (hitstop must not zero the reading), smoothed and printed at
            // most twice a second (old FpsDisplay).
            if (fpsLabel != null && SettingsStore.ShowFps && unscaledDelta > 0f)
            {
                smoothedFps += (1f / unscaledDelta - smoothedFps) * FpsSmoothing;
                fpsElapsed += unscaledDelta;
                if (fpsElapsed >= FpsUpdateInterval)
                {
                    fpsElapsed = 0f;
                    fpsLabel.text = $"{Mathf.RoundToInt(smoothedFps)} FPS";
                }
            }
        }

        // Timer text every frame (the milliseconds move), the clock's rock once a second, the
        // death count only when it changes
        private void RefreshRunStats()
        {
            float seconds = SaveStore.PlaySeconds;
            if (timerLabel != null) timerLabel.text = RunTimeFormat.Format(seconds);

            long whole = (long)seconds;
            if (whole != shownWholeSecond)
            {
                if (shownWholeSecond >= 0 && clockIcon != null && SettingsStore.ShowTimer) UiFx.Wobble(clockIcon);
                shownWholeSecond = whole;
            }

            int deaths = SaveStore.RunDeaths;
            if (deaths == shownDeaths) return;
            if (shownDeaths >= 0 && deaths > shownDeaths && deathIcon != null) UiFx.Bounce(deathIcon);
            shownDeaths = deaths;
            if (deathLabel != null) deathLabel.text = deaths.ToString();
        }

        // ---- Subscriptions ----

        private void ApplyVisibility()
        {
            if (fpsLabel != null)
                fpsLabel.style.display = SettingsStore.ShowFps ? DisplayStyle.Flex : DisplayStyle.None;
            if (timerRow != null)
                timerRow.style.display = SettingsStore.ShowTimer ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>Compact readout for the next FIFO item and current count/capacity
        /// (old InventoryHud.Refresh).</summary>
        private void RefreshInventory()
        {
            if (inventoryRoot == null || inventoryIcon == null || inventoryCount == null) return;

            bool hasItem = InventoryStore.TryPeekFirst(out InventoryItemDefinition first);
            Sprite icon = hasItem ? first.Icon : null;
            if (icon != null) inventoryIcon.style.backgroundImage = new StyleBackground(icon);
            inventoryIcon.style.display = icon != null ? DisplayStyle.Flex : DisplayStyle.None;
            int shownCapacity = Mathf.Max(InventoryStore.Count, InventoryStore.Capacity - heldCapacity);
            inventoryCount.text = $"{InventoryStore.Count}/{shownCapacity}";
        }

        // Rise comes the same frame the store grows (before the panel draws), so the new capacity
        // never shows early. Cancel (the flight torn down) just stops holding it back.
        private void OnCapacityFlight(CapacityFlightPhase phase, int increase)
        {
            switch (phase)
            {
                case CapacityFlightPhase.Rise:
                    heldCapacity += increase;
                    RefreshInventory();
                    break;
                case CapacityFlightPhase.Land:
                    heldCapacity = Mathf.Max(0, heldCapacity - increase);
                    RefreshInventory();
                    CelebrateCapacity(increase);
                    break;
                case CapacityFlightPhase.Cancel:
                    heldCapacity = Mathf.Max(0, heldCapacity - increase);
                    RefreshInventory();
                    break;
            }
        }

        // The crystal hits the plate: the plate pops, the count flashes green, "+N" floats up
        private void CelebrateCapacity(int increase)
        {
            if (inventoryRoot != null) UiFx.Pop(inventoryRoot);
            if (inventoryCount != null)
            {
                inventoryCount.AddToClassList("hud-inventory-count--up");
                inventoryCount.schedule.Execute(() => inventoryCount.RemoveFromClassList("hud-inventory-count--up"))
                    .StartingIn(600);
            }

            if (inventoryPlus == null) return;
            inventoryPlus.text = $"+{increase}";
            UiFx.Tween(inventoryPlus, 0.7f, t => t, t =>
            {
                inventoryPlus.style.opacity = t < 0.15f ? t / 0.15f : 1f - Mathf.Clamp01((t - 0.55f) / 0.45f);
                inventoryPlus.style.translate = new Translate(0f, -28f * Easing.CubeOut(t), 0f);
            }, 0f, () => inventoryPlus.style.opacity = 0f);
        }

        /// <summary>The backpack plate's rectangle in screen pixels (origin bottom left) — where a
        /// capacity crystal flies to. False while the gameplay HUD is hidden or not laid out yet.</summary>
        public bool TryGetInventoryScreenRect(out Rect screenRect)
        {
            screenRect = default;
            if (inventoryRoot?.panel == null) return false;
            if (gameplay != null && gameplay.resolvedStyle.display == DisplayStyle.None) return false;

            Rect plate = inventoryRoot.worldBound;
            Rect panelRect = inventoryRoot.panel.visualTree.worldBound;
            if (panelRect.width <= 0f || panelRect.height <= 0f || float.IsNaN(plate.x) || plate.width <= 0f)
                return false;

            // The panel covers the screen (ScaleWithScreenSize): panel units scale to pixels, and
            // the panel's y runs down where the screen's runs up
            float sx = Screen.width / panelRect.width;
            float sy = Screen.height / panelRect.height;
            screenRect = new Rect(plate.x * sx, Screen.height - plate.yMax * sy, plate.width * sx, plate.height * sy);
            return true;
        }

        private void ShowToast()
        {
            if (saveToast == null) return;
            toastRemaining = ToastHoldSeconds + ToastFadeSeconds;
            saveToast.style.opacity = new StyleFloat(1f);
        }

        /// <summary>Static-event subscriptions must not outlive the UI layer (the spot where the
        /// old MonoBehaviours' OnDisable used to unsubscribe).</summary>
        public void Dispose()
        {
            InventoryStore.Changed -= RefreshInventory;
            ItemBus.CapacityFlight -= OnCapacityFlight;
            SettingsStore.Changed -= ApplyVisibility;
            SaveStore.Saved -= ShowToast;
            tutorialHint.Dispose();
        }
    }
}

using System;
using Inkform.Input;
using Inkform.Settings;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace Inkform.UI
{
    /// <summary>
    /// The CONTROLS section, in groups: DEVICE (input device, rumble level, trigger effects level),
    /// SENSITIVITY, KEY BINDINGS (the active device's rebind table only, each key drawn as its
    /// button icon), GRAPPLING HOOK (the rope gun's snap options the RopeGun reads live from
    /// SettingsStore; adaptive slide speed goes with Wall Snap, no row of its own) and MAINTENANCE last (Unstuck, Reset Bindings, Reset All — out of the way
    /// of a stray press). The section follows a live device switch while the sheet is open:
    /// touching a pad swaps table and icons.
    /// </summary>
    public class SettingsControlsSection : SettingsSection
    {
        // compositePart selects a WASD direction (see BindingTools). Aim is display-only — a
        // mouse-delta binding has nothing meaningful to remap to.
        private struct BindingRow
        {
            public string actionName;
            public string displayName;
            public string compositePart;
            public bool rebindable;

            public BindingRow(string actionName, string displayName, string compositePart, bool rebindable)
            {
                this.actionName = actionName;
                this.displayName = displayName;
                this.compositePart = compositePart;
                this.rebindable = rebindable;
            }
        }

        private static readonly BindingRow[] KbmRows =
        {
            new BindingRow("Move", "Move Up", "up", true),
            new BindingRow("Move", "Move Down", "down", true),
            new BindingRow("Move", "Move Left", "left", true),
            new BindingRow("Move", "Move Right", "right", true),
            new BindingRow("Jump", "Jump", null, true),
            new BindingRow("Dash", "Dash", null, true),
            new BindingRow("RopeFire", "Rope Fire", null, true),
            new BindingRow("SpitBomb", "Spit Bomb", null, true),
            new BindingRow("Aim", "Aim (Mouse)", null, false),
        };

        // Gamepad rows mirror the KBM table; Move / Aim are the sticks — nothing to remap, display only.
        private static readonly BindingRow[] GamepadRows =
        {
            new BindingRow("Move", "Move (Left Stick)", null, false),
            new BindingRow("Aim", "Aim (Right Stick)", null, false),
            new BindingRow("Jump", "Jump", null, true),
            new BindingRow("Dash", "Dash", null, true),
            new BindingRow("RopeFire", "Rope Fire", null, true),
            new BindingRow("SpitBomb", "Spit Bomb", null, true),
        };

        private static readonly SettingsStore.InputDevice[] Devices =
            (SettingsStore.InputDevice[])Enum.GetValues(typeof(SettingsStore.InputDevice));

        private static readonly RumbleLevel[] RumbleLevels = (RumbleLevel[])Enum.GetValues(typeof(RumbleLevel));

        private readonly OptionRow unstuckRow;
        private readonly VisualElement kbmGroup, padGroup;
        private readonly VisualElement[] kbmKeySlots, padKeySlots;
        private readonly Label bindingsHeader;
        private readonly TutorialHintSet glyphSet = TutorialHintSet.Load();

        // What the device content was last drawn for: Tick redraws when either changes
        private SettingsStore.InputDevice shownDevice;
        private PromptScheme shownScheme;

        public SettingsControlsSection(SettingsPanel owner, VisualElement rows) : base(owner, rows, "CONTROLS")
        {
            AddSubHeader("DEVICE");
            AddListRow("Device", Devices, () => SettingsStore.Device, device =>
            {
                SettingsStore.SetDevice(device);
                RefreshDeviceContent();
            }, DeviceText);
            AddListRow("Rumble", RumbleLevels, () => SettingsStore.RumbleLevel, SettingsStore.SetRumbleLevel, RumbleText);
            AddListRow("Trigger Effects", RumbleLevels, () => SettingsStore.TriggerLevel, SettingsStore.SetTriggerLevel, RumbleText);

            AddSubHeader("SENSITIVITY");
            AddSliderRow("Mouse Sensitivity", SettingsStore.MinSensitivity, SettingsStore.MaxSensitivity, SensText,
                () => SettingsStore.MouseSensitivity, SettingsStore.SetMouseSensitivity);
            AddSliderRow("Controller Sensitivity", SettingsStore.MinSensitivity, SettingsStore.MaxSensitivity, SensText,
                () => SettingsStore.StickSensitivity, SettingsStore.SetStickSensitivity);

            // The rebind tables switch with the device
            bindingsHeader = AddSubHeader("KEY BINDINGS");
            kbmGroup = AddGroup();
            padGroup = AddGroup();
            kbmKeySlots = new VisualElement[KbmRows.Length];
            padKeySlots = new VisualElement[GamepadRows.Length];
            BuildRebindTable(kbmGroup, KbmRows, kbmKeySlots, BindingTools.KbmGroup, BindingTools.GamepadGroup);
            BuildRebindTable(padGroup, GamepadRows, padKeySlots, BindingTools.GamepadGroup, BindingTools.KbmGroup);

            AddSubHeader("GRAPPLING HOOK");
            AddOnOffRow("Wall Snap", () => SettingsStore.RopeWallSnap, SettingsStore.SetRopeWallSnap);
            AddOnOffRow("Bomb Snap", () => SettingsStore.RopeBombSnap, SettingsStore.SetRopeBombSnap);
            AddSliderRow("Snap Dead Zone", 0f, 1f, v => v.ToString("0.00"),
                () => SettingsStore.RopeSnapDeadZone, SettingsStore.SetRopeSnapDeadZone);

            AddSubHeader("MAINTENANCE");
            unstuckRow = AddActionRow("Unstuck", () => UI.Unstuck());
            AddActionRow("Reset Bindings", () =>
            {
                BindingTools.ResetAllBindings();
                Refresh();
            });
            AddActionRow("Reset All", owner.ResetAll);
        }

        public override void Refresh()
        {
            base.Refresh();

            // Nothing to un-stick in the menu scene — there is no player there
            unstuckRow.SetEnabled(!UI.IsInMainMenu);

            RefreshDeviceContent();
        }

        // InputHandler / UIManager switch the device on live input: follow it while on stage
        public override void Tick()
        {
            if (SettingsStore.Device != shownDevice || InteractPromptIcons.DetectCurrent() != shownScheme)
                RefreshDeviceContent();
        }

        /// <summary>Which table is shown, and every key icon (the pad face may have changed too —
        /// PlayStation and Xbox draw different buttons).</summary>
        private void RefreshDeviceContent()
        {
            shownDevice = SettingsStore.Device;
            shownScheme = InteractPromptIcons.DetectCurrent();

            bool kbm = shownDevice == SettingsStore.InputDevice.KeyboardMouse;
            if (bindingsHeader != null) bindingsHeader.text = kbm ? "KEY BINDINGS (KEYBOARD)" : "KEY BINDINGS (GAMEPAD)";
            if (kbmGroup != null) kbmGroup.style.display = kbm ? DisplayStyle.Flex : DisplayStyle.None;
            if (padGroup != null) padGroup.style.display = kbm ? DisplayStyle.None : DisplayStyle.Flex;

            for (int i = 0; i < KbmRows.Length; i++)
                RefreshBindingRow(i, KbmRows, kbmKeySlots[i], BindingTools.KbmGroup);
            for (int i = 0; i < GamepadRows.Length; i++)
                RefreshBindingRow(i, GamepadRows, padKeySlots[i], BindingTools.GamepadGroup);
        }

        /// <summary>Builds one rebind table: a row per action, the whole row is the button, the
        /// bound control drawn as icons in the right column. Non-rebindable rows (Aim / the
        /// sticks) go inert instead of hidden, so the table never reflows. Fills
        /// <paramref name="keySlots"/> with each row's icon holder.</summary>
        private void BuildRebindTable(VisualElement group, BindingRow[] table, VisualElement[] keySlots,
            string bindingGroup, string excludeGroup)
        {
            for (int i = 0; i < table.Length; i++)
            {
                int row = i;    // captured per iteration

                var button = new Button { text = string.Empty };
                button.AddToClassList("settings-row");
                button.AddToClassList("option-row");
                button.AddToClassList("floaty");

                var action = new Label(table[i].displayName);
                action.AddToClassList("row-label");
                action.AddToClassList("outline");
                action.pickingMode = PickingMode.Ignore;
                button.Add(action);

                var key = new VisualElement { pickingMode = PickingMode.Ignore };
                key.AddToClassList("row-control");
                key.AddToClassList("row-glyphs");
                button.Add(key);

                group.Add(button);
                keySlots[i] = key;

                if (!table[i].rebindable)
                {
                    button.SetEnabled(false);
                    continue;
                }

                button.clicked += () =>
                {
                    Inkform.Bus.UiBus.RaiseClicked();
                    OnRebindPressed(row, table, key, bindingGroup, excludeGroup);
                };
                button.RegisterCallback<PointerEnterEvent>(_ => Inkform.Bus.UiBus.RaiseHovered());
                button.RegisterCallback<FocusInEvent>(_ => Inkform.Bus.UiBus.RaiseHovered());
                // Left / right have nothing to change here: keep the focus on the row
                button.RegisterCallback<NavigationMoveEvent>(e =>
                {
                    if (e.direction == NavigationMoveEvent.Direction.Left || e.direction == NavigationMoveEvent.Direction.Right)
                        e.StopImmediatePropagation();
                });
            }
        }

        private void OnRebindPressed(int row, BindingRow[] table, VisualElement keySlot, string bindingGroup, string excludeGroup)
        {
            BindingRow cfg = table[row];
            InputAction action = GetAction(cfg.actionName);
            if (action == null) return;

            int index = BindingTools.FindBindingIndex(action, bindingGroup, cfg.compositePart);
            if (index < 0) return;

            BindingTools.StartRebind(action, index, excludeGroup, () => RefreshBindingRow(row, table, keySlot, bindingGroup));
        }

        /// <summary>Draws the row's current binding as its button icon (keycap for keys, pad art
        /// for the active face), from the live effective path so a rebind shows at once.</summary>
        private void RefreshBindingRow(int row, BindingRow[] table, VisualElement keySlot, string bindingGroup)
        {
            if (keySlot == null) return;
            keySlot.Clear();

            BindingRow cfg = table[row];
            InputAction action = GetAction(cfg.actionName);
            int index = action != null ? BindingTools.FindBindingIndex(action, bindingGroup, cfg.compositePart) : -1;
            if (index < 0)
            {
                keySlot.Add(GlyphElements.Create(new InputGlyphs.Glyph { label = "-" }));
                return;
            }

            // The pad table follows the pad in hand; with the keyboard active it shows Xbox faces
            PromptScheme scheme = bindingGroup == BindingTools.KbmGroup ? PromptScheme.KeyboardMouse
                : shownScheme != PromptScheme.KeyboardMouse ? shownScheme : PromptScheme.Xbox;
            keySlot.Add(GlyphElements.Create(InputGlyphs.ResolvePath(
                action.bindings[index].effectivePath, BindingTools.GetDisplay(action, index), scheme, glyphSet)));
        }

        private static string RumbleText(RumbleLevel level) => level switch
        {
            RumbleLevel.Low => "LOW",
            RumbleLevel.Medium => "MEDIUM",
            RumbleLevel.High => "HIGH",
            _ => "OFF",
        };

        private static InputAction GetAction(string actionName)
        {
            var map = InputActions.Wrapper.Player.Get();
            return map != null ? map.FindAction(actionName, false) : null;
        }
    }
}

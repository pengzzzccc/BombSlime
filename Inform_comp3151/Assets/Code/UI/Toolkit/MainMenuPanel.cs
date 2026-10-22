using UnityEngine;
using UnityEngine.UIElements;

namespace Inkform.UI
{
    /// <summary>
    /// Main menu sheet over the cave backdrop, in two states:
    ///  - Title screen (once per session, right after the boot card): the game logo centred with
    ///    "Press any key" under it. Any key, click or pad button moves the logo up and shrinks it
    ///    to its spot above the menu while the menu row and the footer slide in; focus lands on
    ///    BEGIN once the logo has arrived.
    ///  - Menu (every other open: back from options / the save menu / the credits, back from a
    ///    run): the logo is the title, centred in the upper part of the screen; BEGIN, OPTIONS,
    ///    EXIT and CREDITS sit in one row across the lower part.
    /// This panel only reports button presses to the UIManager, which decides everything (same
    /// stance as the old uGUI MainMenuPanel): Play goes through the save menu, Settings opens the
    /// options sheet, Exit quits, Credits rolls the end credits and comes back here.
    /// </summary>
    public class MainMenuPanel : ToolkitPanel
    {
        private const string LogosPath = "UI/BootLogos";

        private const float TitleArmedDelay = 0.25f;    // swallow the input that ended the boot card
        private const float TitleFadeSeconds = 0.6f;
        private const float PromptFadeOutSeconds = 0.15f;
        private const float LogoMoveSeconds = 0.6f;
        private const float MenuEnterDelay = 0.2f;      // the menu follows the logo's departure
        private const float MenuStagger = 0.06f;
        private const float MenuEnterSeconds = 0.3f;
        private const float MenuEnterFromX = -120f;

        // The logo's title-screen spot: centre as a share of the screen, size a whole multiple of
        // the 64 px art (x13; a whole multiple keeps the point-filtered pixels even). Its spot as
        // the menu's title lives in MainMenu.uss (.main-logo: centred, 38% down, x12) — keep the
        // Menu* values equal to it, the tween lands there.
        private const float TitleLogoLeft = 50f;
        private const float TitleLogoTop = 45f;
        private const float TitleLogoSize = 832f;
        private const float MenuLogoLeft = 50f;
        private const float MenuLogoTop = 38f;
        private const float MenuLogoSize = 768f;

        private readonly VisualElement logo;
        private readonly Label pressAnyKey;
        private readonly VisualElement[] menuParts;     // what slides in after the title screen, in order
        private readonly VisualElement[] menuGroups;    // what the title screen hides

        private bool titlePending;      // the next open shows the title screen
        private bool onTitle;           // title screen up, waiting for any key
        private float titleShownAt;

        public MainMenuPanel(VisualElement root, UIManager ui) : base(root, ui)
        {
            Bind(Q<Button>("Btn_Begin"), "Btn_Begin", OnBegin);
            Bind(Q<Button>("Btn_Settings"), "Btn_Settings", OnSettings);
            Bind(Q<Button>("Btn_Exit"), "Btn_Exit", OnExit);
            Bind(Q<Button>("Btn_Credits"), "Btn_Credits", OnCredits);

            // PlayerSettings > bundleVersion, shown bottom left. Missing label is legal (the
            // sheet still works without it), same tolerance as every other Bind.
            Label version = Q<Label>("Lbl_Version");
            if (version != null) version.text = $"v{Application.version}";

            logo = Q("Logo");
            pressAnyKey = Q<Label>("PressAnyKey");
            ApplyLogoArt(Q<Label>("LogoPlaceholder"));

            VisualElement menuColumn = Q("MenuColumn");
            VisualElement footer = Q("Footer");
            menuGroups = new[] { menuColumn, footer };
            menuParts = new[] { Q("Btn_Begin"), Q("Btn_Settings"), Q("Btn_Exit"), Q("Btn_Credits"), footer };
        }

        /// <summary>The next open shows the title screen (UIManager.FinishBoot, once per session).</summary>
        internal void PrepareTitleScreen() => titlePending = true;

        // The menu root has nothing to back out of (UIManager ignores Esc here): Confirm only
        protected override void DefinePrompts(PromptBar bar) => bar.Add(PromptBar.Key.Confirm, "Confirm");

        protected override void OnOpen()
        {
            onTitle = titlePending;
            titlePending = false;
            if (onTitle) ShowTitleScreen();
            else ShowMenu();
        }

        // The title screen fades up in place as the boot card dissolves; the menu slides in
        // like every other sheet
        protected override void PlayEnter()
        {
            if (!onTitle)
            {
                base.PlayEnter();
                return;
            }
            Root.style.translate = StyleKeyword.Null;
            Root.style.opacity = 0f;
            UiFx.Tween(Root, TitleFadeSeconds, t => t, t => Root.style.opacity = t, 0f,
                () => Root.style.opacity = new StyleFloat(1f));
        }

        protected internal override void Tick(float unscaledDelta)
        {
            if (!IsOpen || !onTitle) return;

            // "Press any key" breathes once it has faded up with the sheet
            float since = Time.unscaledTime - titleShownAt;
            float breath = Mathf.Lerp(0.35f, 1f, 0.5f + 0.5f * Mathf.Cos(since * 2.4f));
            pressAnyKey.style.opacity = breath * Mathf.Clamp01(since / TitleFadeSeconds);

            if (since >= TitleArmedDelay && AnyInputPressed()) LeaveTitleScreen();
        }

        // ---- States ----

        private void ShowTitleScreen()
        {
            titleShownAt = Time.unscaledTime;
            foreach (VisualElement group in menuGroups) SetShown(group, false);   // nothing to focus yet
            pressAnyKey.style.display = DisplayStyle.Flex;
            pressAnyKey.style.opacity = 0f;
            PlaceLogo(TitleLogoLeft, TitleLogoTop, TitleLogoSize);
        }

        private void ShowMenu()
        {
            pressAnyKey.style.display = DisplayStyle.None;
            ClearLogoPlacement();
            foreach (VisualElement group in menuGroups) SetShown(group, true);
            foreach (VisualElement part in menuParts)
            {
                if (part == null) continue;
                part.style.translate = StyleKeyword.Null;
                part.style.opacity = StyleKeyword.Null;
            }
        }

        // Any key on the title screen: the prompt fades, the logo rises to its title spot, the menu follows
        private void LeaveTitleScreen()
        {
            onTitle = false;

            float promptFrom = pressAnyKey.resolvedStyle.opacity;
            UiFx.Tween(pressAnyKey, PromptFadeOutSeconds, t => t,
                t => pressAnyKey.style.opacity = Mathf.Lerp(promptFrom, 0f, t), 0f,
                () => pressAnyKey.style.display = DisplayStyle.None);

            UiFx.Tween(logo, LogoMoveSeconds, Easing.CubeInOut, t => PlaceLogo(
                    Mathf.Lerp(TitleLogoLeft, MenuLogoLeft, t),
                    Mathf.Lerp(TitleLogoTop, MenuLogoTop, t),
                    Mathf.Lerp(TitleLogoSize, MenuLogoSize, t)), 0f,
                () =>
                {
                    ClearLogoPlacement();
                    if (IsOpen) FocusFirst();
                });

            foreach (VisualElement group in menuGroups) SetShown(group, true);
            for (int i = 0; i < menuParts.Length; i++)
            {
                if (menuParts[i] == null) continue;
                UiFx.SlideIn(menuParts[i], MenuEnterFromX, MenuEnterSeconds, MenuEnterDelay + i * MenuStagger);
            }
        }

        // ---- Logo ----

        private void ApplyLogoArt(Label placeholder)
        {
            BootLogos logos = Resources.Load<BootLogos>(LogosPath);
            Sprite art = logos != null ? logos.gameLogo : null;
            if (art != null)
            {
                logo.style.backgroundImage = new StyleBackground(art);
                if (placeholder != null) placeholder.style.display = DisplayStyle.None;
            }
            else if (placeholder != null)
            {
                if (logos != null && !string.IsNullOrEmpty(logos.gameTitle)) placeholder.text = logos.gameTitle;
                placeholder.style.display = DisplayStyle.Flex;
            }
        }

        private void PlaceLogo(float leftPercent, float topPercent, float size)
        {
            logo.style.left = Length.Percent(leftPercent);
            logo.style.top = Length.Percent(topPercent);
            logo.style.width = size;
            logo.style.height = size;
        }

        // Back to the USS resting spot above the menu
        private void ClearLogoPlacement()
        {
            logo.style.left = StyleKeyword.Null;
            logo.style.top = StyleKeyword.Null;
            logo.style.width = StyleKeyword.Null;
            logo.style.height = StyleKeyword.Null;
        }

        private static void SetShown(VisualElement el, bool shown)
        {
            if (el != null) el.style.display = shown ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // ---- Buttons ----

        private void OnBegin()
        {
            // Design flow: Play -> save menu (slot selection) -> game.
            UI.Open<SaveMenuPanel>();
        }

        private void OnSettings() => UI.OpenSettings();

        private void OnExit() => UI.Quit();

        // The same roll as the end of a run; from here it returns to this menu, not through a load
        private void OnCredits() => UI.OpenCredits();
    }
}

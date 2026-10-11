using UnityEngine.UIElements;

namespace Inkform.UI
{
    /// <summary>
    /// Pause sheet over the frozen game view: Resume / Options / Save &amp; Quit, sliding in from
    /// the right over a 40% dim like Celeste's OuiOptions. The UIManager drives the pause state
    /// machine (timeScale, input, cursor); this panel only forwards its buttons, exactly like
    /// the old uGUI PausePanel.
    /// </summary>
    public class PausePanel : ToolkitPanel
    {
        public PausePanel(VisualElement root, UIManager ui) : base(root, ui)
        {
            Bind(Q<Button>("Btn_Resume"), "Btn_Resume", OnResume);
            Bind(Q<Button>("Btn_Settings"), "Btn_Settings", OnSettings);
            Bind(Q<Button>("Btn_SaveAndQuit"), "Btn_SaveAndQuit", OnQuitToMainMenu);
        }

        // Esc / pad B on the pause sheet resumes (UIManager's escape stack), so Back reads "Resume"
        protected override void DefinePrompts(PromptBar bar) => bar
            .Add(PromptBar.Key.Confirm, "Confirm")
            .Add(PromptBar.Key.Back, "Resume", UI.Resume);

        private void OnResume() => UI.Resume();

        private void OnSettings() => UI.OpenSettings();

        private void OnQuitToMainMenu() => UI.QuitToMainMenu();
    }
}

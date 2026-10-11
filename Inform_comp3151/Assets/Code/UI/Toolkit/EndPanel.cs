using Inkform.Save;
using UnityEngine.UIElements;

namespace Inkform.UI
{
    /// <summary>
    /// Credits/summary sheet of a finished run, shown in the end scene. Restyled after Celeste's
    /// AreaCompleteTitle: a letter-dropped title over the run's deaths and total time (each with
    /// the HUD's skull / clock icon), with one CONFIRM control, on solid black. Numbers are the
    /// run's own counters — the same clock the HUD showed — painted on open after SaveNow has
    /// stamped them into the slot, so the final room's stretch is included.
    /// </summary>
    public class EndPanel : ToolkitPanel
    {
        public EndPanel(VisualElement root, UIManager ui) : base(root, ui)
        {
            Bind(Q<Button>("Btn_Back"), "Btn_Back", OnBackClicked);
        }

        protected override void OnOpen()
        {
            SaveStore.SaveNow();

            Q<Label>("Lbl_Deaths").text = SaveStore.RunDeaths.ToString();
            Q<Label>("Lbl_Time").text = RunTimeFormat.Format(SaveStore.PlaySeconds);

            UiFx.DropTitle(Q<VisualElement>("TitleRow"), "RUN COMPLETE", 76f);   // --fs-title
        }

        // Esc leaves for the main menu too, exactly like CONFIRM — one prompt says it all
        protected override void DefinePrompts(PromptBar bar) =>
            bar.Add(PromptBar.Key.Confirm, "Confirm", UI.ReturnToMainMenu);

        private void OnBackClicked() => UI.ReturnToMainMenu();
    }
}

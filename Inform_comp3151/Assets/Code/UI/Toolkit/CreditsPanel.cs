using System;
using Inkform.Audio;
using UnityEngine;
using UnityEngine.UIElements;

namespace Inkform.UI
{
    /// <summary>
    /// End credits, opened from the run summary (EndPanel's CONFIRM / Back). The column built from
    /// Resources/UI/CreditsRoll — the game logo, the team by role with concept art between, the
    /// playtesters, a closing line and the final picture — rises from below the screen at the
    /// roll's speed, with its music on the music channel. It stops when the final picture sits
    /// in the middle of the screen, holds there, fades to black with the music, and hands over
    /// to the main menu. Back (Esc / pad B / Start, UIManager's escape stack) skips straight out.
    ///
    /// Driven from Tick on unscaled time like every sheet. The stop point is read from layout
    /// (the final picture's centre in the column), so names and art can change freely in the
    /// asset without retuning anything.
    /// </summary>
    public class CreditsPanel : ToolkitPanel
    {
        private const string LogosPath = "UI/BootLogos";
        private const float EnterFadeSeconds = 0.8f;
        private const float LeaveFadeSeconds = 0.4f;
        private const float SkipGraceSeconds = 0.5f;
        private const float ImageWidth = 1100f;     // concept art, reference pixels
        private const float FinalWidth = 1000f;     // the final picture

        private enum Phase { Rolling, Holding, Fading, Done }

        private readonly VisualElement viewport;
        private readonly VisualElement roll;
        private readonly CreditsRoll content;

        private VisualElement finalPicture;
        private Phase phase;
        private bool started;       // the roll has been placed below the screen (needs the viewport's height)
        private float offset;       // the roll's current vertical translate
        private float holdLeft;

        public CreditsPanel(VisualElement root, UIManager ui) : base(root, ui)
        {
            viewport = Q("Viewport");
            roll = Q("Roll");
            content = CreditsRoll.Load();
            if (content == null)
                Debug.LogWarning($"CreditsPanel: missing Resources/{CreditsRoll.ResourcePath} — the credits are empty and end at once");
        }

        protected override void DefinePrompts(PromptBar bar) => bar.Add(PromptBar.Key.Back, "Skip", Skip);

        /// <summary>Back pressed (or the Skip prompt clicked): leave for the main menu now. A
        /// press in the first moment after opening is ignored, so the Esc that left the summary
        /// (or a double tap of it) cannot skip the roll before it has begun.</summary>
        public void Skip()
        {
            if (Time.unscaledTime - OpenedAtRealtime < SkipGraceSeconds) return;
            Finish();
        }

        // ---- Lifecycle ----

        protected override void OnOpen()
        {
            Build();
            phase = Phase.Rolling;
            started = false;
            if (viewport != null) viewport.style.opacity = new StyleFloat(1f);
            if (roll != null) roll.style.translate = new Translate(0f, 100000f);   // out of sight until placed

            if (content != null && content.music != null) AudioService.PlayMusic(content.music, 1f);
        }

        // Out of black, not a sideways slide: the summary is black too, so the roll seems to begin
        protected override void PlayEnter()
        {
            Root.style.opacity = new StyleFloat(0f);
            UiFx.Tween(Root, EnterFadeSeconds, t => t, v => Root.style.opacity = v);
        }

        protected override void PlayLeave(Action onHidden) =>
            UiFx.Tween(Root, LeaveFadeSeconds, t => t, v => Root.style.opacity = 1f - v, 0f, () =>
            {
                Root.style.opacity = new StyleFloat(1f);   // reset for the next open
                onHidden?.Invoke();
            });

        protected internal override void Tick(float unscaledDelta)
        {
            if (!IsOpen || roll == null || viewport == null || phase == Phase.Done) return;

            float height = viewport.resolvedStyle.height;
            if (float.IsNaN(height) || height <= 0f) return;   // not laid out yet: next frame

            if (!started)
            {
                offset = height;   // the column's top edge at the bottom of the screen
                started = true;
            }

            switch (phase)
            {
                case Phase.Rolling:
                    float speed = content != null ? Mathf.Max(1f, content.scrollSpeed) : 90f;
                    offset -= speed * unscaledDelta;
                    if (TryStopOffset(height, out float stop) && offset <= stop)
                    {
                        offset = stop;
                        phase = Phase.Holding;
                        holdLeft = content != null ? content.holdSeconds : 1f;
                    }
                    break;

                case Phase.Holding:
                    holdLeft -= unscaledDelta;
                    if (holdLeft <= 0f) BeginFade();
                    break;
            }

            roll.style.translate = new Translate(0f, offset);
        }

        // Where the roll stops: the final picture's centre on the viewport's centre. False until
        // the picture has a layout. With no final picture the roll ends once it has fully passed.
        private bool TryStopOffset(float viewportHeight, out float stop)
        {
            stop = 0f;
            if (finalPicture != null)
            {
                Rect box = finalPicture.layout;
                if (float.IsNaN(box.height) || box.height <= 0f) return false;
                stop = viewportHeight * 0.5f - box.center.y;
                return true;
            }

            float rollHeight = roll.layout.height;
            if (float.IsNaN(rollHeight) || rollHeight <= 0f) return false;
            stop = -rollHeight;
            return true;
        }

        private void BeginFade()
        {
            phase = Phase.Fading;
            float fade = content != null ? Mathf.Max(0f, content.fadeSeconds) : 1.2f;
            AudioService.StopMusic(fade);
            // The roll fades over the black sheet, so the screen ends fully black before the
            // scene transition takes over
            UiFx.Tween(viewport, fade, t => t, v => viewport.style.opacity = 1f - v, 0f, Finish);
        }

        private void Finish()
        {
            if (phase == Phase.Done || !IsOpen) return;
            phase = Phase.Done;
            UI.ReturnToMainMenu();
        }

        // ---- Building the column ----

        private void Build()
        {
            if (roll == null) return;
            roll.Clear();
            finalPicture = null;

            AddLogo();
            if (content == null) return;

            foreach (CreditsRoll.Entry entry in content.entries)
            {
                if (entry == null) continue;
                switch (entry.kind)
                {
                    case CreditsRoll.EntryKind.Names: AddNames(entry); break;
                    case CreditsRoll.EntryKind.Image: AddImage(entry.image, ImageWidth, "credits-image"); break;
                    case CreditsRoll.EntryKind.Line: AddLabel(roll, entry.title, "credits-line", "title-font"); break;
                }
            }

            finalPicture = AddImage(content.finalImage, FinalWidth, "credits-image", "credits-final");
        }

        // The game's logo heads the roll, the same art the title screen shows; its title text
        // when there is no art
        private void AddLogo()
        {
            BootLogos logos = Resources.Load<BootLogos>(LogosPath);
            if (logos != null && logos.gameLogo != null)
            {
                var logo = new VisualElement { pickingMode = PickingMode.Ignore };
                logo.AddToClassList("credits-logo");
                logo.style.backgroundImage = new StyleBackground(logos.gameLogo);
                roll.Add(logo);
                return;
            }

            string title = logos != null && !string.IsNullOrEmpty(logos.gameTitle) ? logos.gameTitle : "BOMB SLIME";
            AddLabel(roll, title, "credits-game-title", "title-font", "header-outline");
        }

        private void AddNames(CreditsRoll.Entry entry)
        {
            var block = new VisualElement { pickingMode = PickingMode.Ignore };
            block.AddToClassList("credits-block");
            if (!string.IsNullOrEmpty(entry.title)) AddLabel(block, entry.title.ToUpperInvariant(), "credits-role", "outline");
            if (entry.names != null)
                foreach (string person in entry.names)
                    if (!string.IsNullOrWhiteSpace(person)) AddLabel(block, person, "credits-name", "title-font", "outline");
            roll.Add(block);
        }

        private VisualElement AddImage(Texture2D image, float width, params string[] classes)
        {
            if (image == null) return null;
            var picture = new VisualElement { pickingMode = PickingMode.Ignore };
            foreach (string c in classes) picture.AddToClassList(c);
            picture.style.backgroundImage = Background.FromTexture2D(image);
            // Height from the art's own aspect (an import size cap keeps the aspect)
            picture.style.width = width;
            picture.style.height = image.width > 0 ? width * image.height / image.width : width;
            roll.Add(picture);
            return picture;
        }

        private static void AddLabel(VisualElement parent, string text, params string[] classes)
        {
            var label = new Label(text) { pickingMode = PickingMode.Ignore };
            foreach (string c in classes) label.AddToClassList(c);
            parent.Add(label);
        }
    }
}

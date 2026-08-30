using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-489: what the approval actually puts on screen, read off the laid-out visual tree rather than off the view
/// model. A view-model test can say the sentence is bound and the window still be showing a wall of JSON with a
/// stray line above it — and the guarantee this ticket is about (no text the model wrote) is a guarantee about
/// the screen, not about a property.
/// </summary>
[Collection("avalonia")]
public class PlainConsentSceneTests
{
    private const int Width = 760;
    private const int Height = 520;

    // The `description` the staged call carries: the model's own words about its own request. Nothing on the
    // approval screen may be quoting it.
    private const string WhatTheModelWrote = "Tidying up the inbox, nothing important";

    [Fact]
    public void TheApproval_ReadsAsASentenceAndNamesTheFiles()
    {
        var shown = _Rendered("session-consent-plain");

        Assert.Contains("Move 3 files into ./archive/2026-06", shown);
        Assert.Contains("KPN-2026-06.pdf", shown);
        Assert.Contains("Vattenfall-juni.pdf", shown);
        Assert.Contains("Hosting-Q2.pdf", shown);
    }

    [Theory]
    [InlineData("session-consent-plain")]
    [InlineData("session-consent-plain-developer")]
    public void NothingTheModelWrote_ReachesTheApprovalScreen(string scene)
    {
        var shown = _Rendered(scene);

        // Asserted before the absence, so a scene that rendered nothing at all cannot pass this by being empty.
        Assert.Contains("Move 3 files into ./archive/2026-06", shown);
        Assert.DoesNotContain(WhatTheModelWrote, shown, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCommand_IsFoldedButOnTheScreen()
    {
        var shown = _Rendered("session-consent-plain");

        // Folded: the fold's own label is there, the command it holds is not yet.
        Assert.Contains("Show the command", shown);
        Assert.DoesNotContain("mv KPN-2026-06.pdf", shown, StringComparison.Ordinal);
    }

    [Fact]
    public void OpeningTheFold_ShowsTheCallItself()
    {
        var shown = HeadlessAvalonia.Run(() =>
        {
            var window = Screenshotter.BuildScene("session-consent-plain", Width, Height);
            window.Show();
            try
            {
                _Layout(window);
                foreach (var button in _Transcript(window).GetVisualDescendants().OfType<Button>())
                {
                    if (_TextOf(button).Contains("Show the command", StringComparison.Ordinal))
                    {
                        button.Command?.Execute(button.CommandParameter);
                    }
                }

                _Layout(window);
                return _TextOf(_Transcript(window));
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Contains("mv KPN-2026-06.pdf Vattenfall-juni.pdf Hosting-Q2.pdf ./archive/2026-06/", shown);
        Assert.Contains("Hide the command", shown);
    }

    [Fact]
    public void AtTheDeveloperLevel_TheSentenceSitsAboveTheToolChipRatherThanReplacingIt()
    {
        var shown = _Rendered("session-consent-plain-developer");

        Assert.Contains("Move 3 files into ./archive/2026-06", shown);
        // One screen for both audiences: the chip a developer already reads is still the one on it.
        Assert.Contains("Bash", shown);
        Assert.Contains("mv KPN-2026-06.pdf", shown);
    }

    [Fact]
    public void ACallItCannotRestate_ShowsTheRawCommandInsteadOfAGuess()
    {
        var shown = _Rendered("session-consent-plain-fallback");

        Assert.DoesNotContain("Delete", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("Move", shown, StringComparison.Ordinal);

        // Today's line, and the command still one click away underneath it.
        Assert.Contains("Ran a command — waiting for your approval", shown);
        Assert.Contains("Show the command", shown);
    }

    // Every piece of text the transcript is actually showing, after a frame — hidden branches excluded. Scoped to
    // the transcript because the composer's activity band names the running call at every level, and that is a
    // separate surface which would otherwise answer questions asked about the approval.
    private static string _Rendered(string scene) => HeadlessAvalonia.Run(() =>
    {
        var window = Screenshotter.BuildScene(scene, Width, Height);
        window.Show();
        try
        {
            _Layout(window);
            return _TextOf(_Transcript(window));
        }
        finally
        {
            window.Close();
        }
    });

    private static Visual _Transcript(Window window) =>
        window.GetVisualDescendants().OfType<Control>().First(control => control.Name == "TranscriptItems");

    // A rendered frame, not just a layout pass: the transcript virtualises, so its rows do not exist as controls
    // until something draws them — which is the whole difference between asserting about the screen and
    // asserting about the view model with extra steps.
    private static void _Layout(Window window)
    {
        window.Measure(new Size(Width, Height));
        window.Arrange(new Rect(0, 0, Width, Height));
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
        Dispatcher.UIThread.RunJobs();
    }

    private static string _TextOf(Visual root) => string.Join(
        "\n",
        root.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(block => block.IsEffectivelyVisible)
            .Select(block => block.Text)
            .Where(text => !string.IsNullOrEmpty(text)));
}

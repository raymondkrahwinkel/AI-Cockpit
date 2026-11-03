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

using Avalonia.Controls;

namespace Cockpit.App.ViewTests.Onboarding;

/// <summary>
/// The mechanism <c>App._ShowOnboardingWizard</c> depends on (coordinator finding #3): showing a replacement
/// window from inside another window's <c>Closing</c> handler, before that window has actually gone, so the
/// desktop lifetime never sees zero windows open during the handoff. Nothing else in this codebase uses
/// <c>Window.Closing</c>, so this proves the technique itself works in this Avalonia version rather than trusting
/// it by reading the docs — <c>App</c>'s own version of this can't be run here at all (no
/// <c>IClassicDesktopStyleApplicationLifetime</c> in the headless harness, same gap
/// <c>DialogModalitySplitTests</c> notes for <c>SessionDialogService</c>).
/// </summary>
[Collection("avalonia")]
public class WindowClosingHandoffTests
{
    [Fact]
    public void ClosingAWindow_CanShowAReplacementFromInsideTheClosingHandler_BeforeItActuallyCloses() =>
        HeadlessAvalonia.Run(() =>
        {
            var first = new Window();
            first.Show();

            Window? replacement = null;
            first.Closing += (_, _) => replacement = new Window();

            first.Close();

            Assert.NotNull(replacement);

            replacement.Show();
            Assert.True(replacement.IsVisible);

            replacement.Close();
        });
}

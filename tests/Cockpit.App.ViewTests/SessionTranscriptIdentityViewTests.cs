using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using FluentAssertions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The shared chat transcript (SDK + Local LLM) gained sender identity, a warm empty-state and a "still
/// starting" banner so a reply is plainly the model speaking, a fresh session is an introduced model rather
/// than a black void, and a launching session says so. These render-level checks assert those elements are
/// actually shown (effective visibility), not merely present in the tree — swap them back out and only a test
/// says otherwise.
/// </summary>
[Collection("avalonia")]
public class SessionTranscriptIdentityViewTests
{
    [Fact]
    public void AnAssistantReply_RendersAModelAvatarBadge() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();
        session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "hello"));

        var window = new Window { Width = 800, Height = 600, Content = new SessionView { DataContext = session } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var avatarShown = window.GetVisualDescendants()
            .OfType<Border>()
            .Where(border => border.Classes.Contains("avatarBadge"))
            .Any(border => border.IsEffectivelyVisible);

        window.Close();

        avatarShown.Should().BeTrue("an assistant reply is tagged with the model's avatar so a glance shows who is speaking");
    });

    [Fact]
    public void AnEmptySession_ShowsTheModelCardNotAVoid() => HeadlessAvalonia.Run(() =>
    {
        // A genuinely empty transcript (the sample view model seeds rows) — the empty-state card should stand in.
        var session = new SessionViewModel();
        session.Transcript.Clear();

        var window = new Window { Width = 800, Height = 600, Content = new SessionView { DataContext = session } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // With no rows the only avatar in the tree is the empty-state card's — assert it is actually visible.
        var cardShown = window.GetVisualDescendants()
            .OfType<Border>()
            .Where(border => border.Classes.Contains("avatarBadge"))
            .Any(border => border.IsEffectivelyVisible);

        window.Close();

        cardShown.Should().BeTrue("an empty session fills the void with the model's avatar card, not a blank pane");
    });

    [Fact]
    public void SdkLiveControls_CollapseIntoAChip_NotSpreadAcrossTheHeader() => HeadlessAvalonia.Run(() =>
    {
        // The sample session declares model + effort live controls, as an SDK session does.
        var session = new SessionViewModel();
        session.HasLiveControls.Should().BeTrue("the sample SDK session declares live controls");

        var window = new Window { Width = 1000, Height = 400, Content = new SessionView { DataContext = session } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // The dropdowns now live in a closed flyout opened from one chip — none render inline in the header.
        var combosInHeader = window.GetVisualDescendants().OfType<ComboBox>().Count();
        window.Close();

        combosInHeader.Should().Be(0, "the model/effort/permission dropdowns collapse into a single chip's flyout, not spread across the header");
    });

    [Fact]
    public void ASessionThatIsStillStarting_ShowsAClearStartingIndicator() => HeadlessAvalonia.Run(() =>
    {
        // IsStarting is true from launch until the runtime settles; the banner rides it.
        var session = new SessionViewModel { IsStarting = true };

        var window = new Window { Width = 800, Height = 600, Content = new SessionView { DataContext = session } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var banner = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(t => t.Text != null && t.Text.StartsWith("Starting the session"));
        var bannerShown = banner is not null && banner.IsEffectivelyVisible;
        window.Close();

        bannerShown.Should().BeTrue("a session that is still launching shows a clear 'still starting' indicator, not a silent panel");
    });

    [Fact]
    public void ASessionThatFailedToStart_DoesNotSitStuckOnTheStartingIndicator() => HeadlessAvalonia.Run(() =>
    {
        // A settled (not-starting) session — e.g. a failed launch that reset IsStarting — must not show the banner.
        var session = new SessionViewModel { IsStarting = false };

        var window = new Window { Width = 800, Height = 600, Content = new SessionView { DataContext = session } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var banner = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(t => t.Text != null && t.Text.StartsWith("Starting the session"));
        var bannerShown = banner is not null && banner.IsEffectivelyVisible;
        window.Close();

        bannerShown.Should().BeFalse("the 'still starting' banner shows only while actively launching, never stuck afterwards");
    });
}

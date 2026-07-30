using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.Controls;
using Cockpit.Plugins.Abstractions;
using Xunit.Abstractions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// A plugin's left-menu launcher rendering its badge (AC-516): the accent pill next to the title following
/// <see cref="SideMenuButtonBadge.Changed"/> without polling, the null/zero/two-counter rendering rule (acceptance
/// criterion 3), and — IL#9 — what a one-digit, three-digit and the widest two-counter form actually measure at,
/// so a reviewer can judge whether "100 / 100" still fits the button without deforming it.
/// </summary>
[Collection("avalonia")]
public class PluginLauncherButtonBadgeTests(ITestOutputHelper output)
{
    // Content realises as a visual descendant only once a ContentPresenter has actually applied the button's
    // template (a measure/layout pass) — constructing the control alone is not enough (BuildTraps' "add-a-row"
    // lesson applies just as much to reading a freshly-built control), so every check below shows the button in a
    // real window first.
    [Fact]
    public void NoBadge_RendersNoPill_ExactlyLikeBeforeAC516() => HeadlessAvalonia.Run(() =>
    {
        var launcher = new PluginLauncherButton("Workflows", () => { });
        using var shown = _Show(launcher);

        Assert.Empty(launcher.GetVisualDescendants().OfType<Border>());
    });

    [Fact]
    public void ABadgeWithBothCountersUnknown_RendersNoVisiblePill() => HeadlessAvalonia.Run(() =>
    {
        var badge = new SideMenuButtonBadge();
        var launcher = new PluginLauncherButton("Open PR's", () => { }, badge: badge);
        using var shown = _Show(launcher);

        Assert.False(_Pill(launcher).IsVisible);
    });

    [Theory]
    [InlineData(0, "0")]
    [InlineData(3, "3")]
    [InlineData(100, "100")]
    public void ABadgeWithOnlyPrimary_RendersThatNumber_ZeroIncluded(int primary, string expected) => HeadlessAvalonia.Run(() =>
    {
        var badge = new SideMenuButtonBadge { Primary = primary };
        var launcher = new PluginLauncherButton("Open PR's", () => { }, badge: badge);
        using var shown = _Show(launcher);

        Assert.True(_Pill(launcher).IsVisible);
        Assert.Equal(expected, _PillText(launcher));
    });

    [Fact]
    public void ABadgeWithBothCounters_RendersPrimarySlashSecondary() => HeadlessAvalonia.Run(() =>
    {
        var badge = new SideMenuButtonBadge { Primary = 3, Secondary = 2 };
        var launcher = new PluginLauncherButton("Open PR's", () => { }, badge: badge);
        using var shown = _Show(launcher);

        Assert.Equal("3 / 2", _PillText(launcher));
    });

    // Acceptance criterion 1: the plugin updates the counter after registering, without registering the button
    // again — the host must pick that up on its own.
    [Fact]
    public void ChangingTheBadge_AfterTheButtonIsShown_UpdatesTheRenderedPill_WithoutReregistering() => HeadlessAvalonia.Run(() =>
    {
        var badge = new SideMenuButtonBadge();
        var launcher = new PluginLauncherButton("Open PR's", () => { }, badge: badge);
        using var shown = _Show(launcher);

        Assert.False(_Pill(launcher).IsVisible);

        badge.Primary = 3;
        badge.Secondary = 2;
        Dispatcher.UIThread.RunJobs(); // the change is posted to the UI thread, not applied inline

        Assert.True(_Pill(launcher).IsVisible);
        Assert.Equal("3 / 2", _PillText(launcher));
    });

    // The button unsubscribes on detach (documented on PluginLauncherButton): a menu rebuild that replaces this
    // control must not leave a handler on the old instance permanently listening to a badge it no longer draws.
    [Fact]
    public void DetachingTheButton_StopsFollowingFurtherBadgeChanges() => HeadlessAvalonia.Run(() =>
    {
        var badge = new SideMenuButtonBadge { Primary = 1 };
        var launcher = new PluginLauncherButton("Open PR's", () => { }, badge: badge);
        var shown = _Show(launcher);
        Assert.Equal("1", _PillText(launcher));

        shown.Dispose(); // detaches (closes the window)

        badge.Primary = 99;
        Dispatcher.UIThread.RunJobs();

        // Still "1": the detached button never saw the change. Reading the control directly (not through the
        // closed window) is deliberate — a detached control keeps its last content, it just stops updating it.
        Assert.Equal("1", _PillText(launcher));
    });

    // IL#9: rendered, not reasoned about. Reports the measured widths so a reviewer can judge whether the widest
    // two-counter form ("100 / 100") deforms the button, per Raymond's explicit ask.
    [Fact]
    public void MeasuredWidths_NoBadge_OneDigit_ThreeDigits_AndTheWidestTwoCounterForm() => HeadlessAvalonia.Run(() =>
    {
        double Measure(SideMenuButtonBadge? badge)
        {
            var launcher = new PluginLauncherButton("Open PR's", () => { }, badge: badge);
            using var shown = _Show(launcher);
            launcher.Measure(new Avalonia.Size(400, 100));
            return launcher.DesiredSize.Width;
        }

        var noBadge = Measure(null);
        var unknown = Measure(new SideMenuButtonBadge());
        var oneDigit = Measure(new SideMenuButtonBadge { Primary = 3 });
        var threeDigits = Measure(new SideMenuButtonBadge { Primary = 100 });
        var widestTwoCounter = Measure(new SideMenuButtonBadge { Primary = 100, Secondary = 100 });

        output.WriteLine($"No badge:                {noBadge:F1}px");
        output.WriteLine($"Badge, both unknown:     {unknown:F1}px");
        output.WriteLine($"Badge \"3\":                {oneDigit:F1}px");
        output.WriteLine($"Badge \"100\":              {threeDigits:F1}px");
        output.WriteLine($"Badge \"100 / 100\":        {widestTwoCounter:F1}px");

        // An unknown badge (nothing rendered) must cost nothing over having no badge at all — that is the whole
        // point of the null state.
        Assert.Equal(noBadge, unknown, precision: 1);
        // Each wider form must not be narrower than the one before it.
        Assert.True(oneDigit > unknown);
        Assert.True(threeDigits > oneDigit);
        Assert.True(widestTwoCounter > threeDigits);
    });

    private static Border _Pill(PluginLauncherButton launcher) =>
        launcher.GetVisualDescendants().OfType<Border>().Single();

    private static string _PillText(PluginLauncherButton launcher) =>
        _Pill(launcher).GetVisualDescendants().OfType<TextBlock>().Single().Text ?? string.Empty;

    private static ShownWindow _Show(Control content)
    {
        var window = new Window { Width = 240, Height = 120, Content = content };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return new ShownWindow(window);
    }

    private sealed record ShownWindow(Window Window) : IDisposable
    {
        public void Dispose() => Window.Close();
    }
}

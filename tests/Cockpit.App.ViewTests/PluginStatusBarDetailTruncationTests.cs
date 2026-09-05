using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.Controls;
using Cockpit.Plugins.Abstractions.StatusBar;

namespace Cockpit.App.ViewTests;

// AC-945: a long value (a checkout path) must not push the flyout wider, and the raw value must still be
// reachable somewhere once the display text is shortened.
file sealed class FakeActivitySource(IReadOnlyList<SupervisedActivity> activities) : ISupervisedActivitySource
{
    public string Label => "Fake";

    public IReadOnlyList<SupervisedActivity> Snapshot() => activities;

    public event Action? Changed { add { } remove { } }
}

[Collection("avalonia")]
public sealed class PluginStatusBarDetailTruncationTests
{
    private const string LongCheckoutPath =
        @"C:\Users\raymo\AppData\Roaming\Cockpit\worktrees\d8bcc995e0e5\cockpit-default-f9007ce5-some-very-long-checkout-directory-name-that-keeps-going-and-going";

    // _BuildPanel is private, and reflection is the only way to it: every other flyout test in this project reads
    // shown content off `Button.Flyout` (set in XAML), but this host opens its Flyout with ShowAt on a locally
    // created instance that is never assigned to the button, so headless there is no public handle back to it.
    private static Control _BuildPanel(ISupervisedActivitySource source)
    {
        var method = typeof(PluginStatusBarHost).GetMethod("_BuildPanel", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (Control)method.Invoke(null, [source, new Flyout()])!;
    }

    private static Control _PanelWithLongCheckoutPath()
    {
        var activity = new SupervisedActivity(
            "job-1",
            "job-1 (local)",
            [new ActivityDetail("Checkout", LongCheckoutPath)],
            () => Task.CompletedTask);
        return _BuildPanel(new FakeActivitySource([activity]));
    }

    private static TextBlock _ShowAndFindCheckoutDetail(Control panel)
    {
        var window = new Window { Content = panel };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        return panel.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text!.StartsWith("Checkout:"));
    }

    [Fact]
    public void LongCheckoutPath_IsShortenedRatherThanRenderedInFull() => HeadlessAvalonia.Run(() =>
    {
        var detailText = _ShowAndFindCheckoutDetail(_PanelWithLongCheckoutPath());

        // A bounded display length is what keeps the flyout from being pushed wide by a raw path (the panel's
        // own layout, kill button and margins already account for the rest of its width).
        Assert.True(detailText.Text!.Length < LongCheckoutPath.Length / 2, $"displayed text was {detailText.Text}");
    });

    [Fact]
    public void LongCheckoutPath_StaysReachableThroughTheTooltip() => HeadlessAvalonia.Run(() =>
    {
        var detailText = _ShowAndFindCheckoutDetail(_PanelWithLongCheckoutPath());

        Assert.DoesNotContain(LongCheckoutPath, detailText.Text);
        Assert.Equal(LongCheckoutPath, ToolTip.GetTip(detailText));
    });
}

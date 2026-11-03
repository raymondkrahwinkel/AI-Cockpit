using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.Controls;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// A plugin's left-menu launcher rendering its badge (AC-516): the pill following
/// <see cref="SideMenuButtonBadge.Changed"/> without polling, and the null/zero rendering rule (acceptance criterion 3).
/// </summary>
[Collection("avalonia")]
public class PluginLauncherButtonBadgeTests
{
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

    private static Border _Pill(PluginLauncherButton launcher) =>
        launcher.GetVisualDescendants().OfType<Border>().Single(border => border.Child is TextBlock);

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

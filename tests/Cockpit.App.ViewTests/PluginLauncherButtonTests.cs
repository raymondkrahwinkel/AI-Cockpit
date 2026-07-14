using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.Controls;
using FluentAssertions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The gear on a plugin's left-menu button (#: settings from anywhere). It sits inside the launcher, and Click is
/// a bubbling routed event — so the press that opens a plugin's settings would, left alone, also reach the launcher
/// it sits in and open the plugin's dialog behind them. That is invisible from the outside (both things "work"),
/// which is why it is tested rather than looked at.
/// <para>
/// The button is shown in a real window, because that is what these tests are actually about: a routed event travels
/// the <em>visual</em> tree, so a launcher that was only constructed — never realised — bubbles nothing, and a test
/// against it would pass whether the guard is there or not. (A window brings a compositor the GC tears down off-thread,
/// which is why this lives in its own assembly.)
/// </para>
/// </summary>
[Collection("avalonia")]
public class PluginLauncherButtonTests
{
    [Fact]
    public void PressingTheGear_OpensSettingsAndLeavesTheLauncherAlone() => HeadlessAvalonia.Run(() =>
    {
        var invoked = 0;
        var settingsOpened = 0;
        var launcher = new PluginLauncherButton("YouTrack", () => invoked++, () => settingsOpened++);
        using var shown = _Show(launcher);

        _Gear(launcher).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        settingsOpened.Should().Be(1);
        invoked.Should().Be(0, "the press stops at the gear — it must not also run the plugin's own action");
    });

    [Fact]
    public void PressingTheLauncher_RunsThePluginsAction() => HeadlessAvalonia.Run(() =>
    {
        var invoked = 0;
        var launcher = new PluginLauncherButton("YouTrack", () => invoked++, () => { });
        using var shown = _Show(launcher);

        launcher.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        invoked.Should().Be(1);
    });

    // A plugin with no settings view gets no gear: one that opened nothing would be exactly the dead control the
    // cockpit does not ship.
    [Fact]
    public void APluginWithoutSettings_HasNoGear() => HeadlessAvalonia.Run(() =>
    {
        var launcher = new PluginLauncherButton("Prompt Library", () => { });

        launcher.GetLogicalDescendants().OfType<Button>().Should().BeEmpty();
    });

    private static Button _Gear(PluginLauncherButton launcher) =>
        launcher.GetVisualDescendants().OfType<Button>().Single();

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

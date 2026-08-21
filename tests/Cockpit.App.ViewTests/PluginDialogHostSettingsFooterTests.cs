using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Cockpit.App.Plugins;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The standalone plugin-settings window — a plugin's own gear and a widget pane's gear both end up here
/// (<c>OpenPluginSettingsAsync</c> / <c>ShowWidgetSettingsAsync</c>), and it is the host that has no transaction
/// to wait for: it stages and commits on the same click. Pins that half of the staged contract (AC-1003) —
/// nothing is written when the view refuses, the view's own reason is what the operator reads (it used to be a
/// generic host line, AC-499), and the write only happens through the host.
/// </summary>
[Collection("avalonia")]
public class PluginDialogHostSettingsFooterTests
{
    private sealed class FakeSettingsView(string? refusal = null) : UserControl, IPluginSettingsView
    {
        public int Committed { get; private set; }

        public bool TryStage(out Action? commit, out string? error)
        {
            if (refusal is not null)
            {
                commit = null;
                error = refusal;
                return false;
            }

            commit = () => Committed++;
            error = null;
            return true;
        }
    }

    [Fact]
    public void ARefusedSave_ShowsTheViewsOwnReason_WritesNothing_AndLeavesTheWindowOpen() => HeadlessAvalonia.Run(() =>
    {
        var window = new Window();
        window.Show();
        var saved = 0;
        var view = new FakeSettingsView(refusal: "\"work\" is used by another row above.");
        var footer = PluginDialogHost.BuildSettingsFooter(window, view, () => saved++);
        var status = _Status(footer);

        Assert.False(status.IsVisible);

        _Button(footer, "Save").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.True(status.IsVisible);
        Assert.Equal("\"work\" is used by another row above.", status.Text);
        Assert.Equal(0, view.Committed);
        Assert.Equal(0, saved);
        Assert.True(window.IsVisible);
    });

    [Fact]
    public void ARefusalWithNoReason_StillSaysSomething() => HeadlessAvalonia.Run(() =>
    {
        var window = new Window();
        window.Show();
        var footer = PluginDialogHost.BuildSettingsFooter(window, new FakeSettingsView(refusal: "  "), onSaved: null);

        _Button(footer, "Save").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        var status = _Status(footer);
        Assert.True(status.IsVisible);
        Assert.False(string.IsNullOrWhiteSpace(status.Text));
        Assert.True(window.IsVisible);
    });

    [Fact]
    public void AnAcceptedSave_CommitsThroughTheHost_RunsOnSaved_AndClosesTheWindow() => HeadlessAvalonia.Run(() =>
    {
        var window = new Window();
        window.Show();
        var saved = 0;
        var view = new FakeSettingsView();
        var footer = PluginDialogHost.BuildSettingsFooter(window, view, () => saved++);
        var status = _Status(footer);

        _Button(footer, "Save").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(1, view.Committed);
        Assert.Equal(1, saved);
        Assert.False(status.IsVisible);
        Assert.False(window.IsVisible);
    });

    // AC-1004, criterion 5: the settings-saved signal follows the write, and it is this window's job to keep the
    // two together even though it performs them on one click — a plugin invalidating a cache from that signal
    // (Docker, LocalCi, Kubernetes, GitHub PR all do) would otherwise rebuild against the values being replaced.
    [Fact]
    public void TheSettingsSavedSignalRunsAfterTheWrite_NotBeforeIt() => HeadlessAvalonia.Run(() =>
    {
        var window = new Window();
        window.Show();
        var order = new List<string>();
        var view = new OrderedSettingsView(order);
        var footer = PluginDialogHost.BuildSettingsFooter(window, view, () => order.Add("notified"));

        _Button(footer, "Save").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(["write", "notified"], order);
    });

    [Fact]
    public void AViewWithNoSaveCapability_GetsOnlyClose_AndNoFallbackLine() => HeadlessAvalonia.Run(() =>
    {
        var window = new Window();
        var footer = PluginDialogHost.BuildSettingsFooter(window, new UserControl(), onSaved: null);

        Assert.Single(footer.GetVisualDescendants().OfType<Button>());
        Assert.False(_Status(footer).IsVisible);
    });

    private sealed class OrderedSettingsView(List<string> order) : UserControl, IPluginSettingsView
    {
        public bool TryStage(out Action? commit, out string? error)
        {
            commit = () => order.Add("write");
            error = null;
            return true;
        }
    }

    private static Button _Button(Control footer, string content) =>
        footer.GetVisualDescendants().OfType<Button>().Single(button => (string?)button.Content == content);

    private static TextBlock _Status(Control footer) =>
        footer.GetVisualDescendants().OfType<TextBlock>().Single();
}

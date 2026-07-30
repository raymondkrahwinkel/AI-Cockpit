using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Cockpit.App.Plugins;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The plugin-settings footer's Save button used to answer a refused save with nothing at all: <see cref="IPluginSettingsView.Save"/>
/// only ever returns true/false, and a false answer closed no window, showed no message, and told the operator
/// nothing had happened at all — found when Depot's own save (a separate change) started refusing a whole batch on
/// a name collision instead of silently dropping one row. This pins the host-level fallback line
/// <see cref="PluginDialogHost.BuildSettingsFooter"/> now shows instead of staying silent.
/// </summary>
[Collection("avalonia")]
public class PluginDialogHostSettingsFooterTests
{
    private sealed class FakeSettingsView : UserControl, IPluginSettingsView
    {
        private readonly bool _saves;

        public FakeSettingsView(bool saves) => _saves = saves;

        public bool Save() => _saves;
    }

    [Fact]
    public void ARefusedSave_ShowsAFallbackReason_AndLeavesTheWindowOpen() => HeadlessAvalonia.Run(() =>
    {
        var window = new Window();
        window.Show();
        var saved = 0;
        var footer = PluginDialogHost.BuildSettingsFooter(window, new FakeSettingsView(saves: false), () => saved++);
        var status = _Status(footer);

        Assert.False(status.IsVisible);

        _Button(footer, "Save").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.True(status.IsVisible);
        Assert.False(string.IsNullOrEmpty(status.Text));
        Assert.Equal(0, saved);
        Assert.True(window.IsVisible);
    });

    [Fact]
    public void ASuccessfulSave_RunsOnSavedAndClosesTheWindow_WithoutShowingTheFallback() => HeadlessAvalonia.Run(() =>
    {
        var window = new Window();
        window.Show();
        var saved = 0;
        var footer = PluginDialogHost.BuildSettingsFooter(window, new FakeSettingsView(saves: true), () => saved++);
        var status = _Status(footer);

        _Button(footer, "Save").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(1, saved);
        Assert.False(status.IsVisible);
        Assert.False(window.IsVisible);
    });

    [Fact]
    public void AViewWithNoSaveCapability_GetsOnlyClose_AndNoFallbackLine() => HeadlessAvalonia.Run(() =>
    {
        var window = new Window();
        var footer = PluginDialogHost.BuildSettingsFooter(window, new UserControl(), onSaved: null);

        Assert.Single(footer.GetVisualDescendants().OfType<Button>());
        Assert.False(_Status(footer).IsVisible);
    });

    private static Button _Button(Control footer, string content) =>
        footer.GetVisualDescendants().OfType<Button>().Single(button => (string?)button.Content == content);

    private static TextBlock _Status(Control footer) =>
        footer.GetVisualDescendants().OfType<TextBlock>().Single();
}

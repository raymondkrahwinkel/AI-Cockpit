using Avalonia.Controls;
using Cockpit.App.Plugins;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

[Collection("avalonia")]
public class OptionsPluginLazyConstructionTests
{
    [Fact]
    public void BeginningOptionsEdit_DoesNotConstructPluginSettingsViews() => HeadlessAvalonia.Run(() =>
    {
        var constructed = 0;
        var cockpit = new CockpitViewModel();
        var plugins = (IPluginContributionSink)cockpit;
        plugins.AddPluginSettings("first", "First", () =>
        {
            constructed++;
            return new TextBlock();
        });
        plugins.AddPluginSettings("second", "Second", () =>
        {
            constructed++;
            return new TextBlock();
        });

        cockpit.BeginOptionsEdit();

        Assert.Equal(0, constructed);
    });

    [Fact]
    public void SelectingAPlugin_ConstructsOnlyItsSettingsView() => HeadlessAvalonia.Run(() =>
    {
        var constructed = 0;
        var cockpit = new CockpitViewModel();
        var plugins = (IPluginContributionSink)cockpit;
        plugins.AddPluginSettings("first", "First", () =>
        {
            constructed++;
            return new TextBlock();
        });
        plugins.AddPluginSettings("second", "Second", () =>
        {
            constructed++;
            return new TextBlock();
        });
        cockpit.BeginOptionsEdit();

        var dialog = new OptionsDialog { DataContext = cockpit };
        dialog.Show();
        dialog.UpdateLayout();
        dialog.SelectCategory("plugin:first");

        Assert.Equal(1, constructed);
        dialog.Close();
    });
}

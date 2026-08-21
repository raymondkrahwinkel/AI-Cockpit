using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.Docking;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Plugins.Abstractions.Docking;
using Material.Icons;

namespace Cockpit.App.ViewTests;

// AC-960 criterion 6: a restored `OpenDockPanelId` a plugin has not (yet, or no longer) registered must not
// leave the rail expanded onto a blank panel — and criterion 5's other half, a late registration for that same
// id must still fill it in, the way the Assistant already does after AC-953.
[Collection("avalonia")]
public sealed class DockPanelUnresolvedIdTests
{
    private static DockPanelRegistration _Panel(string id) =>
        new(id, "T", MaterialIconKind.ViewDashboardOutline, () => new TextBlock { Text = id });

    [Fact]
    public async Task UnresolvedOpenPanelId_CollapsesTheRail_WithNoContent()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var panels = new DockPanelRegistry();
            panels.Register(_Panel("other"));
            var cockpit = new CockpitViewModel(panels) { OpenDockPanelId = "missing" };

            var main = new Window { Width = 1100, Height = 760, Content = new CockpitView { DataContext = cockpit } };
            main.Show();
            main.UpdateLayout();
            await Task.Delay(50);

            try
            {
                var content = main.GetVisualDescendants().OfType<ContentControl>().First(c => c.Name == "DockPanelContent");
                Assert.Null(content.Content);

                var rail = main.GetVisualDescendants().OfType<Grid>().First(g => g.Name == "RootGrid").ColumnDefinitions[4];
                Assert.Equal(40, rail.Width.Value);
            }
            finally
            {
                main.Close();
            }
        });
    }

    [Fact]
    public async Task ALateRegistrationForTheOpenId_ExpandsTheRail_AndFillsTheContent()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var panels = new DockPanelRegistry();
            panels.Register(_Panel("other"));
            var cockpit = new CockpitViewModel(panels) { OpenDockPanelId = "later", DockRailWidth = 400 };

            var main = new Window { Width = 1100, Height = 760, Content = new CockpitView { DataContext = cockpit } };
            main.Show();
            main.UpdateLayout();

            try
            {
                var rail = main.GetVisualDescendants().OfType<Grid>().First(g => g.Name == "RootGrid").ColumnDefinitions[4];
                Assert.Equal(40, rail.Width.Value);

                panels.Register(_Panel("later"));
                main.UpdateLayout();
                await Task.Delay(50);

                Assert.Equal(400, rail.Width.Value);
                var content = main.GetVisualDescendants().OfType<ContentControl>().First(c => c.Name == "DockPanelContent");
                Assert.IsType<TextBlock>(content.Content);
            }
            finally
            {
                main.Close();
            }
        });
    }
}

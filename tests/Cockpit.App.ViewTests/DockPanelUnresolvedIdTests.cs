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
    public async Task ALateRegistrationForTheOpenId_ExpandsTheRail_AndFillsTheContent()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var panels = new DockPanelRegistry();
            // AC-951: the first registration of an id wins and a later one is refused.
            Assert.True(panels.Register(_Panel("other")));
            Assert.False(panels.Register(_Panel("other")));
            var cockpit = new CockpitViewModel(panels) { OpenDockPanelId = "later", DockRailWidth = 400 };

            var main = new Window { Width = 1100, Height = 760, Content = new CockpitView { DataContext = cockpit } };
            main.Show();
            main.UpdateLayout();

            try
            {
                // Unresolved: the rail stays collapsed with nothing in it (criterion 6) until the late registration.
                var rail = main.GetVisualDescendants().OfType<Grid>().First(g => g.Name == "PanelsRoot").ColumnDefinitions[4];
                Assert.Equal(40, rail.Width.Value);
                var content = main.GetVisualDescendants().OfType<ContentControl>().First(c => c.Name == "DockPanelContent");
                Assert.Null(content.Content);

                // DockPanelRegistry.Changed fires OnPropertyChanged(DockPanels)/(HasDockPanels) synchronously off
                // Register, and the bound rail width reacts to that on the same call stack — UpdateLayout alone
                // already arranges against the new value, so there is nothing async here to wait out.
                panels.Register(_Panel("later"));
                main.UpdateLayout();

                Assert.Equal(400, rail.Width.Value);
                Assert.IsType<TextBlock>(content.Content);
            }
            finally
            {
                main.Close();
            }
        });
    }
}

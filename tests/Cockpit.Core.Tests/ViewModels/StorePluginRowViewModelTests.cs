using Cockpit.App.ViewModels;
using Cockpit.Core.Plugins;

namespace Cockpit.Core.Tests.ViewModels;

public class StorePluginRowViewModelTests
{
    // Core unit: an installed plugin with an incompatible newer version remains an Update action, but says why it is disabled.
    [Fact]
    public void AnIncompatibleUpdate_StaysVisibleWithTheHostVersionReason()
    {
        var version = new PluginStoreVersion("2.0.0", "plugins/example-2.0.0.zip", null, "0.154", null, null);
        var entry = new PluginStoreEntry("example", "Example", null, "Cockpit", "2.0.0", [version]);
        var row = new StorePluginRowViewModel(entry, PluginStoreConfig.Remote("https://store.example/index.json"), "1.0.0", hostVersion: new Version(0, 152, 0));

        Assert.Equal((false, "Update", "Requires Wispslate Cockpit 0.154 or later; this machine runs 0.152.0"),
            (row.CanTakePrimaryAction, row.PrimaryActionLabel, row.PrimaryActionTooltip));
    }
}

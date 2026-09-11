using Avalonia;
using Avalonia.Controls;
using Cockpit.Core.Configuration;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-1299: the macOS application menu's About entry carries the product's name, not the framework's. Avalonia.Native
/// only picks up a menu that is already on the Application when its exporter constructs (right after
/// <c>Initialize()</c>), so the entry has to be there — and named — before any service exists.
/// </summary>
[Collection("avalonia")]
public class ApplicationMenuTests
{
    [Fact]
    public void AboutEntryCarriesTheProductName() => HeadlessAvalonia.Run(() =>
    {
        var menu = NativeMenu.GetMenu(Application.Current!);
        Assert.NotNull(menu);

        var about = Assert.IsType<NativeMenuItem>(menu.Items[0]);
        Assert.Equal($"About {CockpitProduct.DisplayName}", about.Header);
    });
}

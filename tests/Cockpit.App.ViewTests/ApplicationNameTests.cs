using Avalonia;
using Cockpit.Core.Configuration;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-690: macOS builds its Apple-menu/About/Hide/Quit labels from <c>Application.Name</c>, not the app
/// bundle's Info.plist — left unset it reads "Avalonia Application".
/// </summary>
[Collection("avalonia")]
public class ApplicationNameTests
{
    [Fact]
    public void ApplicationNameIsCockpitsOwn() => HeadlessAvalonia.Run(() =>
    {
        Assert.Equal(CockpitProduct.DisplayName, Application.Current?.Name);
    });
}

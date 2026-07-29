using Cockpit.App.Services;
using Cockpit.Core.Tests.Voice;
using Cockpit.Core.Toasts;

namespace Cockpit.Core.Tests.Toasts;

/// <summary>
/// <see cref="ToastService"/> forwards to the cockpit's <see cref="Cockpit.App.ViewModels.ToastHostViewModel"/>
/// (#61). Driven via <see cref="ToastService.ShowOnUiThread"/> directly — the public <see cref="ToastService.Show"/>
/// dispatcher-marshaling wrapper is not exercised here for the same reason the voice coordinators' event
/// handlers aren't: pumping a real Avalonia dispatcher loop in a unit test is not practical.
/// </summary>
public class ToastServiceTests
{
    [Fact]
    public void ShowOnUiThread_AddsTheToastToTheCockpitsToastHost()
    {
        var cockpit = TestCockpit.NewViewModel();
        var service = new ToastService(cockpit);

        service.ShowOnUiThread("Plugin update available", ToastSeverity.Information, "View", null);

        Assert.Single(cockpit.Toasts);
        Assert.Equal("Plugin update available", cockpit.Toasts[0].Message);
        Assert.Equal(ToastSeverity.Information, cockpit.Toasts[0].Severity);
        Assert.Equal("View", cockpit.Toasts[0].ActionLabel);
    }
}

using Cockpit.App.ViewModels;
using Cockpit.Core.Toasts;

namespace Cockpit.App.ViewTests;

public sealed class ToastHostViewModelTests
{
    public static IEnumerable<object[]> AutoDismissCases() =>
    [
        [ToastSeverity.Information, false, true],
        [ToastSeverity.Information, true, false],
        [ToastSeverity.Success, false, true],
        [ToastSeverity.Success, true, false],
        [ToastSeverity.Warning, false, false],
        [ToastSeverity.Warning, true, false],
        [ToastSeverity.Error, false, false],
        [ToastSeverity.Error, true, false],
    ];

    [Theory]
    [MemberData(nameof(AutoDismissCases))]
    public void Add_AutoDismissesOnlyPlainInformationAndSuccessToasts(ToastSeverity severity, bool hasAction, bool shouldAutoDismiss)
    {
        var scheduled = false;
        var host = new ToastHostViewModel((_, _) => scheduled = true);

        host.Add("toast", severity, hasAction ? [new ToastAction("Act", static () => { })] : [], sticky: false);

        Assert.Equal(shouldAutoDismiss, scheduled);
    }

    // Layer 2, view model (AC-1802): the panel's button is the toast's own, and a repeated update check stays one item.
    [Fact]
    public void Add_KeepsEachToastLive_InTheNotificationCenter_OneItemPerKey()
    {
        var chosen = 0;
        var host = new ToastHostViewModel((_, _) => { });

        host.Add("14 settings changed", ToastSeverity.Warning, [new ToastAction("Choose…", () => chosen++)], sticky: false, source: "Settings sync");
        var item = Assert.Single(host.Notifications.Items);
        Assert.Equal(("Settings sync", 1), (item.Source, host.Notifications.Badge.Count));

        item.Actions[0].InvokeCommand.Execute(null);

        Assert.Equal((1, true, 0), (chosen, item.IsHandled, host.Notifications.Badge.Count));
        Assert.Empty(host.Toasts);

        host.Add("0.127.0 is out", ToastSeverity.Information, [new ToastAction("Open it", static () => { })], sticky: false, key: "cockpit.update");
        host.Add("0.128.0 is out", ToastSeverity.Information, [new ToastAction("Open it", static () => { })], sticky: false, key: "cockpit.update");

        Assert.Equal(["0.128.0 is out", "14 settings changed"], host.Notifications.Items.Select(kept => kept.Message));
    }
}

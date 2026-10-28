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
}

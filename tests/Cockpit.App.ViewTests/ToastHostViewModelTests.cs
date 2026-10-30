using Cockpit.App.ViewModels;
using Cockpit.Core.Toasts;

namespace Cockpit.App.ViewTests;

public sealed class ToastHostViewModelTests
{
    private static void _ConditionUpdatesOneToast_TimesOutToHistory_ResolvesAndDisablesActions()
    {
        var scheduled = new List<(ToastViewModel Toast, TimeSpan Delay)>();
        var host = new ToastHostViewModel((toast, delay) => scheduled.Add((toast, delay)))
        {
            AutoDismissDelay = TimeSpan.FromSeconds(12),
        };
        var applies = true;
        var invoked = false;
        var first = host.AddCondition("memory:one", "over cap", ToastSeverity.Warning,
            [new ToastAction("Kill", () => invoked = true)], () => applies);
        Assert.InRange(Assert.Single(scheduled).Delay.TotalSeconds, 11, 12);
        host.AddCondition("memory:one", "still over cap", ToastSeverity.Warning,
            [new ToastAction("Kill", () => invoked = true)], () => applies);
        Assert.Single(host.Toasts);
        Assert.Equal("still over cap", Assert.Single(host.Notifications.Items).Message);
        Assert.False(first.Actions[0].InvokeCommand.CanExecute(null));
        scheduled[1].Toast.CloseCommand.Execute(null);
        Assert.Empty(host.Toasts);
        Assert.Single(host.Notifications.Items);
        host.AddCondition("memory:one", "updated after timeout", ToastSeverity.Warning,
            [new ToastAction("Kill", () => invoked = true)], () => applies);
        Assert.Empty(host.Toasts);
        applies = false;
        host.RecheckConditions();
        var kept = Assert.Single(host.Notifications.Items);
        Assert.True(kept.IsHandled);
        Assert.False(kept.Actions[0].InvokeCommand.CanExecute(null));
        kept.Actions[0].Invoke();
        Assert.False(invoked);
    }

    private static void _ConditionRecoveryOrSessionRemovalClosesBeforeTimeout_RecurrenceCanNotifyAgain()
    {
        var host = new ToastHostViewModel((_, _) => { });
        var memory = 11;
        var present = true;
        host.AddCondition("memory:one", "over cap", ToastSeverity.Warning, [], () => present && memory > 10);
        memory = 10;
        host.RecheckConditions();
        Assert.Empty(host.Toasts);
        Assert.True(Assert.Single(host.Notifications.Items).IsHandled);
        memory = 11;
        host.AddCondition("memory:one", "over again", ToastSeverity.Warning, [], () => present && memory > 10);
        Assert.Single(host.Toasts);
        present = false;
        host.RecheckConditions();
        Assert.Empty(host.Toasts);
        Assert.True(Assert.Single(host.Notifications.Items).IsHandled);
    }

    [Fact]
    public void NotificationsRespectTimeoutsDedupAndConditionLifetime()
    {
        _ConditionUpdatesOneToast_TimesOutToHistory_ResolvesAndDisablesActions();
        _ConditionRecoveryOrSessionRemovalClosesBeforeTimeout_RecurrenceCanNotifyAgain();
        Assert.True(_AutoDismisses(ToastSeverity.Information, false));
        Assert.False(_AutoDismisses(ToastSeverity.Information, true));
        Assert.True(_AutoDismisses(ToastSeverity.Success, false));
        Assert.False(_AutoDismisses(ToastSeverity.Success, true));
        Assert.False(_AutoDismisses(ToastSeverity.Warning, false));
        Assert.False(_AutoDismisses(ToastSeverity.Warning, true));
        Assert.False(_AutoDismisses(ToastSeverity.Error, false));
        Assert.False(_AutoDismisses(ToastSeverity.Error, true));
    }

    private static bool _AutoDismisses(ToastSeverity severity, bool hasAction)
    {
        var scheduled = false;
        var host = new ToastHostViewModel((_, _) => scheduled = true);

        host.Add("toast", severity, hasAction ? [new ToastAction("Act", static () => { })] : [], sticky: false);

        return scheduled;
    }
}

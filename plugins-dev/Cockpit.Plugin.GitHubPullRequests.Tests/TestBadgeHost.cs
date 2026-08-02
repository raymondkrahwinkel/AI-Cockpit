using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;

namespace Cockpit.Plugin.GitHubPullRequests.Tests;

// A minimal `ICockpitHost` for exercising `PullRequestBadgeUpdater`: records every
// `AddSideMenuButtonWithBadge` registration and `ShowToast` call, and every other member
// throws — the updater touches none of them during construction or a snapshot update.
internal sealed class TestBadgeHost : ICockpitHost
{
    // Simulates an older host whose copy of Cockpit.Plugins.Abstractions predates AC-516 — the exact failure the
    // updater's own try/catch exists for. Configurable because the realistic failure is a
    // `TypeLoadException` on the missing `SideMenuButtonBadge` type itself, not only a
    // `MissingMethodException` on the method that returns it — both are proven, not just the one an
    // exception is easiest to throw from a test double.
    public Func<Exception>? BadgeUnsupportedException { get; init; }

    public List<string> RegisteredBadgeTitles { get; } = [];

    // The instance returned by the one (real) `AddSideMenuButtonWithBadge` call, so a test can read back what the updater set on it.
    public SideMenuButtonBadge? LastBadge { get; private set; }

    public List<string> Toasts { get; } = [];

    // Every `(title, singleInstanceKey)` pair a call to the keyed `ShowDialogAsync` overload was made with — what the badge's own click handler is supposed to reach.
    public List<(string Title, string SingleInstanceKey)> DialogsShown { get; } = [];

    // The `onInvoke` the one (real) `AddSideMenuButtonWithBadge` call was given — a test drives this directly to prove what clicking the badge actually does, since nothing else in this fake simulates a pointer click.
    public Action? BadgeClicked { get; private set; }

    public IServiceProvider Services => throw new NotSupportedException();

    public ICockpitActions Actions => throw new NotSupportedException();

    public IPluginStorage Storage => throw new NotSupportedException();

    public void AddSettings(Func<Control> createView) => throw new NotSupportedException();

    public void AddSideMenuButton(string title, Action onInvoke) => throw new NotSupportedException();

    public void AddSideMenuSection(string title, Func<Control> createView) => throw new NotSupportedException();

    public SideMenuButtonBadge AddSideMenuButtonWithBadge(string title, Action onInvoke)
    {
        if (BadgeUnsupportedException is { } makeException)
        {
            throw makeException();
        }

        RegisteredBadgeTitles.Add(title);
        BadgeClicked = onInvoke;
        LastBadge = new SideMenuButtonBadge();
        return LastBadge;
    }

    public Task ShowDialogAsync(string title, Func<Control> createContent, double width = 720, double height = 560) =>
        Task.CompletedTask;

    public Task ShowDialogAsync(string title, Func<Control> createContent, string singleInstanceKey, double width = 720, double height = 560)
    {
        DialogsShown.Add((title, singleInstanceKey));
        return Task.CompletedTask;
    }

    public void ShowToast(string message, PluginToastSeverity severity = PluginToastSeverity.Information, string? actionLabel = null, Action? onAction = null) =>
        Toasts.Add(message);
}

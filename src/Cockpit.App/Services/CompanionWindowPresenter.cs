using Avalonia.Threading;
using Cockpit.App.Plugins;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Layout;

namespace Cockpit.App.Services;

// AC-237: lazily creates the single shared `CompanionWindow`, mirroring `VoiceOverlayPresenter`. Unlike the
// overlay it also tracks visibility across restarts (the operator can leave it open) and rebuilds the hosted
// tools whenever a plugin registers one after the window already exists.
internal sealed class CompanionWindowPresenter : ISingletonService
{
    private readonly ICompanionToolRegistry _registry;
    private readonly ILayoutSettingsStore _layoutSettingsStore;
    private CompanionWindow? _window;
    private CompanionOrientation _orientation = CompanionOrientation.Horizontal;
    private bool _alwaysOnTop = true;

    // These show as the view itself on the strip; the conversation and screenshot pop-outs come with their own tickets.
    private static readonly HashSet<string> InlineToolIds = ["cockpit.assistant", "cockpit.screenshot"];

    public CompanionWindowPresenter(ICompanionToolRegistry registry, ILayoutSettingsStore layoutSettingsStore)
    {
        _registry = registry;
        _layoutSettingsStore = layoutSettingsStore;
        _registry.Changed += (_, _) => _Refresh();
        _registry.PopOutRequested += (_, toolId) => Dispatcher.UIThread.Post(() => _window?.OpenPopOut(toolId));
    }

    public bool IsVisible => _window?.IsVisible == true;

    // Test seam, same shape as AssistantIndicatorCoordinator.OpenChatWindow: what a test asserts the hosted tools
    // and chrome against, without this presenter exposing a public window-manipulation surface.
    internal CompanionWindow? Window => _window;

    public void Show()
    {
        var isNew = _window is null;
        var window = _window ??= new CompanionWindow();
        if (isNew)
        {
            window.CloseRequested += (_, _) => Hide();
            window.LayoutChanged += (_, _) => _OnLayoutChanged(window);
            window.ApplyLayout(_orientation, _alwaysOnTop);
            _Refresh();
        }

        window.Show();
        _ = _SaveVisibleAsync(true);
    }

    public void Hide()
    {
        if (_window is { } window)
        {
            _ = window.SavePositionAsync();
        }

        _window?.ClosePopOuts();
        _window?.Hide();
        _ = _SaveVisibleAsync(false);
    }

    public void Toggle()
    {
        if (IsVisible)
        {
            Hide();
        }
        else
        {
            Show();
        }
    }

    // Reopens the window if it was left open at last exit. Called once at startup, after plugin phase 2 has had a
    // chance to register tools (App._StartCockpit), so the first paint already shows them instead of an empty card.
    public async Task RestoreAsync()
    {
        var settings = await _layoutSettingsStore.LoadAsync().ConfigureAwait(false);
        _orientation = settings.CompanionOrientation;
        _alwaysOnTop = settings.CompanionAlwaysOnTop;
        if (settings.CompanionWindowVisible)
        {
            await Dispatcher.UIThread.InvokeAsync(Show);
        }
    }

    private void _Refresh()
    {
        if (_window is not { } window)
        {
            return;
        }

        // CreateContext never returns null here: every id in _registry.Tools is one _registry itself just
        // reported, so the lookup inside it cannot miss. The assistant stands first, the rest in registration order.
        window.SetTools([.. _registry.Tools
            .OrderBy(tool => tool.Id != "cockpit.assistant")
            .Select(tool => new CompanionStripItem(
                tool.Id, tool.Title, tool.Tooltip, tool.Icon, tool.IconKind, _registry.GetBadge(tool.Id),
                () => tool.CreateView(_registry.CreateContext(tool.Id)!), InlineToolIds.Contains(tool.Id)))]);
    }

    private void _OnLayoutChanged(CompanionWindow window)
    {
        _orientation = window.Orientation;
        _alwaysOnTop = window.AlwaysOnTop;
        _ = _SaveLayoutAsync();
    }

    private async Task _SaveLayoutAsync()
    {
        try
        {
            var settings = await _layoutSettingsStore.LoadAsync().ConfigureAwait(false);
            await _layoutSettingsStore.SaveAsync(settings with { CompanionOrientation = _orientation, CompanionAlwaysOnTop = _alwaysOnTop })
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best-effort, like the visibility save below: a lost preference must not stop the strip from changing.
        }
    }

    private async Task _SaveVisibleAsync(bool visible)
    {
        try
        {
            var settings = await _layoutSettingsStore.LoadAsync().ConfigureAwait(false);
            await _layoutSettingsStore.SaveAsync(settings with { CompanionWindowVisible = visible }).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best-effort: a failed save must not stop the window from actually showing/hiding.
        }
    }
}

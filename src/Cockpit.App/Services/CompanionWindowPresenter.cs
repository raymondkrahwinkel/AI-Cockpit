using Avalonia.Threading;
using Cockpit.App.Plugins;
using Cockpit.App.Views;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Toasts;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Layout;

namespace Cockpit.App.Services;

// AC-237: lazily creates the single shared `CompanionWindow`, mirroring `VoiceOverlayPresenter`. Unlike the
// overlay it also tracks visibility across restarts (the operator can leave it open) and rebuilds the hosted
// tools whenever a plugin registers one after the window already exists.
internal sealed partial class CompanionWindowPresenter : ISingletonService
{
    private readonly ICompanionToolRegistry _registry;
    private readonly ILayoutSettingsStore _layoutSettingsStore;
    private readonly CompanionAttentionFeed _attention;
    private CompanionWindow? _window;
    private CompanionOrientation _orientation = CompanionOrientation.Horizontal;
    private bool _alwaysOnTop = true;

    // These show as the view itself on the strip; the conversation and screenshot pop-outs come with their own tickets.
    private static readonly HashSet<string> InlineToolIds = ["cockpit.assistant", "cockpit.screenshot"];
    private static readonly HashSet<string> OwnHeadToolIds = [CompanionAttentionFeed.StatusToolId, CompanionAttentionFeed.ToolId];

    public CompanionWindowPresenter(ICompanionToolRegistry registry, ILayoutSettingsStore layoutSettingsStore, CompanionAttentionFeed attention, IToastService toasts, Func<CockpitViewModel> cockpit, ILogger<CompanionWindowPresenter> logger)
    {
        _attention = attention;
        _toasts = toasts;
        _cockpit = cockpit;
        _logger = logger;
        _customization.Changed += _OnCustomizationChanged;
        _registry = registry;
        _layoutSettingsStore = layoutSettingsStore;
        _registry.Changed += (_, _) => _OnRegistryChanged();
        _registry.PopOutRequested += (_, toolId) => Dispatcher.UIThread.Post(() =>
        {
            // A window that is not always on top may sit under another one; a new question brings it back without focus.
            if (toolId == CompanionAttentionFeed.ToolId && _window is { IsVisible: true, Topmost: false } window)
            {
                window.Topmost = true;
                window.Topmost = false;
            }

            _window?.OpenPopOut(toolId);
        });
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
            window.ConfigureCustomization(_customization, _EnableToolAsync, _SourceOf);
            window.CloseRequested += (_, _) => Hide();
            window.LayoutChanged += (_, _) => _OnLayoutChanged(window);
            window.ApplyLayout(_orientation, _alwaysOnTop);
            _Refresh();
        }

        window.Show();
        _ = _SaveVisibleAsync(true);
    }

    // AC-2209: the operator hid the strip, which ends a look-along: its timer and Stop would be out of sight.
    public event EventHandler? Hidden;

    public void Hide()
    {
        Hidden?.Invoke(this, EventArgs.Empty);
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
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _orientation = settings.CompanionOrientation;
            _alwaysOnTop = settings.CompanionAlwaysOnTop;
            _customization.Restore(settings.CompanionStripLayout);
            _preferencesLoaded = true;
            _Refresh();
            if (settings.CompanionWindowVisible)
            {
                Show();
            }
        });
    }

    private void _ApplyTools()
    {
        if (_window is not { } window)
        {
            return;
        }
        var tools = _customization.Tools.ToDictionary(tool => tool.Id);
        window.SetTools([.. _customization.OnStrip.Select(id => tools[id])
            .Select(tool => new CompanionStripItem(
                tool.Id, tool.Title, tool.Tooltip, tool.Icon, tool.IconKind, _registry.GetBadge(tool.Id),
                () => _registry.CreateContext(tool.Id) is { } context ? tool.CreateView(context) : new Avalonia.Controls.TextBlock { Text = "Tool unavailable" },
                InlineToolIds.Contains(tool.Id), tool.Id == CompanionAttentionFeed.StatusToolId ? () => new CompanionStatusCounters(_attention) : null,
                OwnHeadToolIds.Contains(tool.Id)))]);
    }

    private void _OnLayoutChanged(CompanionWindow window)
    {
        _orientation = window.Orientation;
        _alwaysOnTop = window.AlwaysOnTop;
        _ = _SaveLayoutAsync();
    }

    private Task _SaveLayoutAsync() => _SavePreferencesAsync();

    private Task _SaveVisibleAsync(bool visible) => _SavePreferencesAsync(visible);
}

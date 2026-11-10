using Avalonia.Threading;
using Cockpit.App.ViewModels;
using Cockpit.Core.Toasts;
using Cockpit.Plugins.Abstractions.CompanionTools;
using Cockpit.Core.Abstractions.Toasts;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.Services;

internal sealed partial class CompanionWindowPresenter
{
    private readonly CompanionCustomizationModel _customization = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly IToastService _toasts;
    private readonly Func<CockpitViewModel> _cockpit;
    private readonly ILogger<CompanionWindowPresenter> _logger;
    private bool _preferencesLoaded;

    private void _OnRegistryChanged()
    {
        var tools = _registry.Tools;
        if (Dispatcher.UIThread.CheckAccess())
        {
            _Refresh(tools);
        }
        else
        {
            Dispatcher.UIThread.Post(() => _Refresh(tools));
        }
    }

    private void _Refresh(IReadOnlyList<CompanionToolRegistration>? tools = null)
    {
        if (!_preferencesLoaded)
        {
            return;
        }
        var removed = _customization.Merge(tools ?? _registry.Tools, _registry.GetPluginId);
        _ApplyTools();
        if (removed.Count > 0)
        {
            var message = $"{string.Join(", ", removed)} is no longer available";
            if (_window is { IsVisible: true } visible)
            {
                if (!visible.ShowUnavailableNotice(message))
                {
                    _customization.Notice = message;
                }
            }
            else
            {
                _toasts.Show(message, ToastSeverity.Information);
            }
        }
        _ = _SaveLayoutAsync();
    }

    // Where a strip item comes from, as Customize shows it: the plugin's name, or Cockpit for the host's own.
    private string _SourceOf(string id) => _customization.PluginId(id) is not { } plugin
        ? "Cockpit"
        : _cockpit().Plugins.Plugins.FirstOrDefault(row => row.FolderId == plugin)?.DisplayName ?? plugin;

    private void _OnCustomizationChanged()
    {
        _ApplyTools();
        _ = _SaveLayoutAsync();
    }

    private async Task _EnableToolAsync(string id)
    {
        try
        {
            var plugins = _cockpit().Plugins;
            await plugins.LoadAsync();
            var row = plugins.Plugins.FirstOrDefault(plugin => plugin.FolderId == _customization.PluginId(id));
            if (row is not null && plugins.EnablePluginCommand.CanExecute(row) && row.CanEnable)
            {
                await plugins.EnablePluginCommand.ExecuteAsync(row);
                _customization.Notice = plugins.StatusMessage;
            }
            else
            {
                _customization.Notice = "This plugin cannot be enabled here. Check its status or reinstall it in Plugins.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enable a companion tool's plugin");
            _customization.Notice = "The plugin could not be enabled. Try again or check Plugins.";
        }
    }

    private async Task _SavePreferencesAsync(bool? visible = null)
    {
        var layout = _customization.Layout;
        var orientation = _orientation;
        var alwaysOnTop = _alwaysOnTop;
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var settings = await _layoutSettingsStore.LoadAsync().ConfigureAwait(false);
            await _layoutSettingsStore.SaveAsync(visible is { } show
                ? settings with { CompanionWindowVisible = show }
                : settings with { CompanionOrientation = orientation, CompanionAlwaysOnTop = alwaysOnTop, CompanionStripLayout = layout })
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save companion preferences");
            Dispatcher.UIThread.Post(() => _customization.Notice = "Your strip changed, but its preferences could not be saved. Try again.");
        }
        finally
        {
            _saveGate.Release();
        }
    }
}

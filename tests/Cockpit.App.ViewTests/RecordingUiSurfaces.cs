using Avalonia.Controls;
using Cockpit.App.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.StatusBar;

namespace Cockpit.App.ViewTests;

// The window side a plugin's UI part reaches through CockpitUiHost: the contribution sink, the dialog host and the
// selected session, recording what the plugin asked for so a test can read it back and answer a dialog like an operator.
internal sealed class RecordingUiSurfaces : IPluginContributionSink, IPluginDialogHost, IPluginActiveSession, IPluginWindowActions
{
    public List<ToolbarAction> ToolbarActions { get; } = [];

    // Called with the dialog's title, content factory and the key the plugin gave it (unscoped).
    public Func<string, Func<Control>, string, Task> OnDialog { get; set; } = (_, _, _) => Task.CompletedTask;

    public string? ActivePaneId { get; set; }

    public string? ActiveSessionWorkingDirectory => null;

    public SessionUsageSnapshot? ActiveSessionUsage => null;

    public event EventHandler? ActiveSessionChanged
    {
        add { }
        remove { }
    }

    public event EventHandler? ActiveSessionUsageChanged
    {
        add { }
        remove { }
    }

    public void AddToolbarAction(string pluginId, ToolbarAction action) => ToolbarActions.Add(action);

    public Task ShowDialogAsync(string title, Func<Control> createContent, double width, double height, Func<Task>? onOpenSettings = null, string? singleInstanceKey = null) =>
        OnDialog(title, createContent, (singleInstanceKey ?? string.Empty)[((singleInstanceKey ?? string.Empty).IndexOf(':') + 1)..]);

    public Task ShowSettingsDialogAsync(string title, Func<Control> createView, double width, double height, Action? onSaved = null, string? singleInstanceKey = null) =>
        Task.CompletedTask;

    public Task SetClipboardTextAsync(string text) => Task.CompletedTask;

    public Task<bool> ConfirmAsync(string title, string message, string confirmLabel = "Confirm") => Task.FromResult(true);

    public void AddPluginSideSection(string pluginId, string title, Func<Control> createView)
    {
    }

    public void AddPluginSideButton(string pluginId, string title, Action onInvoke)
    {
    }

    public void AddPluginSessionHeaderItem(Func<IPluginSessionContext, Control> createView)
    {
    }

    public void AddPluginSessionBannerItem(Func<IPluginSessionContext, Control> createView)
    {
    }

    public void AddPluginSessionHeaderAction(PluginSessionAction action)
    {
    }

    public void AddSupervisedActivityProvider(ISupervisedActivitySource source)
    {
    }

    public void AddPluginShortcut(PluginShortcut shortcut)
    {
    }

    public void AddPluginSettings(string pluginId, string pluginName, Func<Control> createView)
    {
    }

    public bool HasPluginSettings(string pluginId) => false;

    public Task OpenPluginSettingsAsync(string pluginId) => Task.CompletedTask;

    public void AddSettingsSavedHandler(string pluginId, Action callback)
    {
    }

    public void NotifySettingsSaved(string pluginId)
    {
    }

    public void ApplyPluginMenuPreference(string pluginId, int menuOrder, bool hiddenInMenu)
    {
    }
}

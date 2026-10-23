using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Schedules.UI;

// The UI part of Schedules (AC-1546): a plain plugin button under Plugins, as Workflows and Autopilot have, so it
// follows MenuOrder, can be pinned to the sidebar by the operator (not pinned by default, B8) and has its own shortcut,
// editable in Options → Shortcuts. The engine runs in the backend part, with this screen closed or never opened.
public sealed class SchedulesUi : ICockpitPluginUi
{
    public const string ShortcutId = "schedules.open";
    public const string DefaultGesture = "Ctrl+Shift+S";

    public void InitializeUi(ICockpitUiHost host)
    {
        host.AddSideMenuButton("Schedules", () => _Open(host));
        host.AddShortcut(new PluginShortcut(ShortcutId, "Schedules", DefaultGesture, () => _Open(host)));
    }

    // A menu click cannot await, so the screen is awaited here and a failure to open it is told rather than lost.
    private static async void _Open(ICockpitUiHost host)
    {
        try
        {
            await OpenAsync(host);
        }
        catch (Exception exception)
        {
            host.ShowToast($"Could not open Schedules: {exception.Message}", PluginToastSeverity.Error);
        }
    }

    internal static async Task OpenAsync(ICockpitUiHost host)
    {
        using var state = new SchedulesState(host.Channel);
        await state.RefreshAsync();
        var profiles = (await host.GetProfilesAsync()).Select(profile => profile.Label).ToList();
        await host.ShowDialogAsync("Schedules", () => new SchedulesScreen(state, host, profiles), "schedules", width: 1280, height: 820);
    }
}

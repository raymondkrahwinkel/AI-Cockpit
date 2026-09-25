using System.Text.Json;
using Cockpit.Plugin.LocalCi.Contracts;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.UI;
using Material.Icons;

namespace Cockpit.Plugin.LocalCi.UI;

// The UI part of Local CI (AC-1394): the settings page, a session's run dialog and its header badge. The backend
// part, LocalCiPlugin, keeps the runtime probe, the runner, the tracker and the MCP tools, and answers this part's
// questions over the plugin's channel.
public sealed class LocalCiUi : ICockpitPluginUi
{
    public void InitializeUi(ICockpitUiHost host)
    {
        host.AddSettings(() => new LocalCiSettingsControl(host));

        // Built once per session panel, which is the only place a session's own context arrives — so it is both
        // where the last run is shown and where the pane-to-checkout answer the MCP tools need is learned (see
        // Sessions/SessionCheckouts on the backend part).
        host.AddSessionHeaderItem(session =>
        {
            _RememberCheckout(host, session);
            session.WorkingDirectoryChanged += (_, _) => _RememberCheckout(host, session);
            return new LocalCiSessionBadge(host, session);
        });

        // From the session's own header, so the run is about the checkout that session is working in rather than
        // whichever pane happens to be selected when the operator gets to it.
        host.AddSessionHeaderAction(new PluginSessionAction(
            "Run CI on this machine…",
            "",
            session => _ = _OpenForAsync(host, session))
        {
            IconKind = MaterialIconKind.FlaskOutline,
        });
    }

    private static Task _OpenForAsync(ICockpitUiHost host, IPluginSessionContext session)
    {
        if (session.WorkingDirectory is not { Length: > 0 } projectRoot)
        {
            host.ShowToast(
                "This session has not said which directory it is working in yet, so there is no checkout to run.",
                PluginToastSeverity.Warning);
            return Task.CompletedTask;
        }

        return host.ShowDialogAsync(
            "Local CI",
            () => new LocalCiRunView(host, projectRoot),
            singleInstanceKey: $"run.{session.PaneId}",
            width: 900,
            height: 640);
    }

    // Best-effort: the checkout map is only what the MCP tools' pane-to-checkout lookup falls back to, so a failed
    // send here means one tool call later reports "no checkout" — not a fault this header item can show.
    private static void _RememberCheckout(ICockpitUiHost host, IPluginSessionContext session)
    {
        if (session.PaneId is not { Length: > 0 } paneId)
        {
            return;
        }

        var payload = JsonSerializer.SerializeToElement(
            new LocalCiCheckoutInfo(paneId, session.WorkingDirectory), LocalCiChannel.Json);
        _ = _SendAsync(host, payload);
    }

    private static async Task _SendAsync(ICockpitUiHost host, JsonElement payload)
    {
        try
        {
            await host.Channel.InvokeAsync(LocalCiChannel.RememberCheckout, payload);
        }
        catch (Exception)
        {
            // Best-effort bookkeeping — see _RememberCheckout.
        }
    }
}

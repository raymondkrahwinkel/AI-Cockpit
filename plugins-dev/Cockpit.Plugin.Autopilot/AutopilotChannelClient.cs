using System.Diagnostics;
using System.Text.Json;
using Avalonia.Threading;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.Workspaces;
using static Cockpit.Plugin.Autopilot.AutopilotChannelContract;

namespace Cockpit.Plugin.Autopilot;

// AC-1418: the UI half of Autopilot's channel. A backend that went away (the plugin disabled under an open
// workspace) leaves the surface inert and traced, never a crash.
internal sealed class AutopilotChannelClient(IPluginUiChannel channel)
{
    // Follows one workspace's State on the UI thread, newest seq only (F1.4): a snapshot is the whole state, so one
    // that arrives after a newer one is stale and dropped. Checked on the UI thread, where the state is applied.
    public IDisposable FollowState(string workspaceId, Action<AutopilotWorkspaceState> apply)
    {
        long last = 0;
        return channel.Subscribe(State, channelEvent =>
        {
            var state = Read<AutopilotWorkspaceState>(channelEvent.Payload);
            if (state.WorkspaceId != workspaceId)
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (channelEvent.Seq <= last)
                {
                    return;
                }

                last = channelEvent.Seq;
                apply(state);
            });
        });
    }

    // A nudge that the template list changed; the reader asks for it again with TemplatesAsync.
    public IDisposable OnTemplatesChanged(Action changed) =>
        channel.Subscribe(TemplatesChanged, _ => Dispatcher.UIThread.Post(changed));

    public Task<AutopilotTemplateCatalog?> TemplatesAsync() => _AskAsync<AutopilotTemplateCatalog>(Templates, true);

    public Task<PluginRememberedWorkingPaths?> RememberedPathsAsync() => _AskAsync<PluginRememberedWorkingPaths>(RememberedPaths, true);

    // ponytail: waits for the answer, which in-process is already there; a remote backend (F5/F6) needs the settings
    // view to build its tracker rows after an await first.
    public IReadOnlyList<string> TrackerIds() => _AskAsync<List<string>>(Trackers, true).GetAwaiter().GetResult() ?? [];

    public async Task<bool> BeginPlanningAsync() => await _AskAsync<bool?>(BeginPlanning, true) ?? false;

    public Task<string?> EmbedPlanningCeoAsync(PlanningCeoRequest request) => _AskAsync<string>(EmbedPlanningCeo, request);

    // Fire-and-forget: what the operator did; the State that follows shows its effect.
    public void Send(string action, object? payload = null) => _ = _AskAsync<JsonElement>(action, payload ?? true);

    private async Task<T?> _AskAsync<T>(string action, object payload)
    {
        try
        {
            return (await channel.InvokeAsync(action, ToJson(payload))).Deserialize<T>(Json);
        }
        catch (Exception failure)
        {
            Trace.TraceWarning($"Autopilot: the channel action '{action}' failed: {failure.Message}");
            return default;
        }
    }
}

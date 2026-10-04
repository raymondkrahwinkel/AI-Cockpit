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
        var following = new Following();
        following.Subscription = channel.Subscribe(State, channelEvent =>
        {
            var state = Read<AutopilotWorkspaceState>(channelEvent.Payload);
            if (state.WorkspaceId != workspaceId)
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (following.Disposed || channelEvent.Seq <= last)
                {
                    return;
                }

                last = channelEvent.Seq;
                apply(state);
            });
        });
        return following;
    }

    // A nudge that the template list changed; the reader asks for it again with TemplatesAsync.
    public IDisposable OnTemplatesChanged(Action changed)
    {
        var following = new Following();
        following.Subscription = channel.Subscribe(TemplatesChanged, _ => Dispatcher.UIThread.Post(() =>
        {
            if (!following.Disposed)
            {
                changed();
            }
        }));
        return following;
    }

    public Task<AutopilotTemplateCatalog?> TemplatesAsync() => _AskAsync<AutopilotTemplateCatalog>(Templates, true);

    public Task<PluginRememberedWorkingPaths?> RememberedPathsAsync() => _AskAsync<PluginRememberedWorkingPaths>(RememberedPaths, true);

    public async Task<IReadOnlyList<string>> TrackerIdsAsync() => await _AskAsync<List<string>>(Trackers, true) ?? [];

    public async Task<bool> BeginPlanningAsync() => await _AskAsync<bool?>(BeginPlanning, true) ?? false;

    public async Task<bool> SubmitAsync(SubmitRequest request) => await _AskAsync<bool?>(Submit, request) ?? false;

    public Task<string?> EmbedPlanningCeoAsync(PlanningCeoRequest request) => _AskAsync<string>(EmbedPlanningCeo, request);

    // Fire-and-forget: what the operator did; the State that follows shows its effect.
    public void Send(string action, object? payload = null) => _ = _AskAsync<JsonElement>(action, payload ?? true);

    // A subscription whose callbacks, already posted to the UI thread, do nothing once it is disposed (AC-1418).
    private sealed class Following : IDisposable
    {
        public IDisposable? Subscription { get; set; }

        public bool Disposed { get; private set; }

        public void Dispose()
        {
            Disposed = true;
            Subscription?.Dispose();
        }
    }

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

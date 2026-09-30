using Microsoft.Extensions.DependencyInjection;
using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Delegation;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions.Permissions;

namespace Cockpit.Journeys;

// J5, bulk work handed on: a session delegates a task to another profile on the real `cockpit-orchestrator` endpoint
// and `get_task_result` hands back that profile's answer. Asked for no permission, the task runs read-only.
[Collection(JourneyCollection.Alone)]
public sealed class DelegationJourney
{
    private const string Caller = "journey-caller";
    private const string Delegate = "Local";

    [Fact]
    public async Task ASessionDelegatesToAnotherProfile_AndGetsItsAnswer_ReadOnly()
    {
        await using var cockpit = JourneyHost.Api();
        await cockpit.Services.GetRequiredService<ISessionProfileStore>().SaveAsync(
        [
            new SessionProfile(Delegate, new ClaudeConfig(Path.Combine(cockpit.StateRoot, ".claude")), Delegation: new DelegationPolicy(AllowedAsTarget: true))
            {
                DefaultKind = ProfileSessionKind.Sdk,
            },
        ]);
        cockpit.Backend.Start();
        var url = cockpit.Services.GetRequiredService<IOrchestratorServerState>().OrchestratorMcpUrl ?? "";

        // One client for both calls: a task is found only by the session that delegated it.
        await using var session = await cockpit.ConnectAsPaneAsync(Caller, DelegationMcp.ServerName, url);
        // Every parameter the tool's schema requires, as a model sends them; no `requested_permission` is the read-only ask.
        var delegated = await JourneyHost.CallAsync(session, "delegate_task", new()
        {
            ["profile"] = Delegate,
            ["prompt"] = "summarise the log",
            ["task_type"] = null,
            ["label"] = null,
            ["working_directory"] = null,
            ["requested_permission"] = null,
            ["mcp_servers"] = null,
        });
        await cockpit.Driver.Answered.WaitAsync(Until.Ceiling);
        var answer = await JourneyHost.CallAsync(session, "get_task_result", new() { ["task_id"] = delegated["TaskId"]?.GetValue<string>() });

        Assert.Equal(
            ("Completed", "echo: summarise the log", DelegatedToolPermissionPolicy.ReadOnlyCeiling),
            (answer["status"]?.GetValue<string>(), answer["result"]?.GetValue<string>(), answer["permission"]?.GetValue<string>()));
        Assert.Equal((DelegatedToolPermissionPolicy.ReadOnlyCeiling, Delegate), (cockpit.Driver.PermissionMode, cockpit.Driver.Profile?.Label));
    }
}

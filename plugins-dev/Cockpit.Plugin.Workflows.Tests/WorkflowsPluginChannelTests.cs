using System.Text.Json;
using Cockpit.Plugin.Workflows.Contracts;
using Cockpit.Plugin.Workflows.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.Plugin.Workflows.Tests;

// AC-1399: the editor saves and runs a flow over the plugin's channel, and the backend part runs it with the watcher's
// engine, keeps the run, and announces it to the UI part's run panel.
public class WorkflowsPluginChannelTests
{
    [Fact]
    public async Task AFlowSavedAndRunOverTheChannel_IsKept_AndItsRunAnnounced()
    {
        var channel = new InProcessChannel();
        var storage = new InMemoryPluginStorage();
        var host = Substitute.For<ICockpitHost>();
        host.Channel.Returns(channel);
        host.Storage.Returns(storage);
        host.Cache.Returns(storage);
        host.Sessions.OpenSessions.Returns([new OpenCockpitSession("pane-2", "api")]);
        using var plugin = new WorkflowsPlugin();
        plugin.Initialize(host);
        var flow = new Workflow
        {
            Id = "f",
            Name = "Label it",
            Nodes =
            {
                new WorkflowNode { Id = "t", TypeId = "cockpit.manual", Name = "Start" },
                new WorkflowNode { Id = "s", TypeId = "cockpit.set-status", Name = "Label", Parameters = { ["Session"] = "api", ["Status"] = "AC-1399" } },
            },
            Connections = { new WorkflowConnection { FromNodeId = "t", FromOutput = 0, ToNodeId = "s" } },
        };

        await channel.InvokeAsync(WorkflowsChannel.Save, _Json(new WorkflowsSaveRequest(WorkflowJson.WriteAll([flow]))));
        var answer = await channel.InvokeAsync(WorkflowsChannel.Run, _Json(new WorkflowsRunRequest(WorkflowJson.Write(flow), "t")));
        var lastRun = await channel.InvokeAsync(WorkflowsChannel.LastRun, _Json(new WorkflowsLastRunRequest("f")));

        var run = answer.Deserialize<WorkflowRun>(WorkflowsChannel.Json);
        Assert.Equal(RunStatus.Succeeded, run?.Status);
        Assert.Equal("Label it", Assert.Single(new WorkflowStore(storage).Load()).Name);
        Assert.Equal(run?.Id, lastRun.Deserialize<WorkflowRun>(WorkflowsChannel.Json)?.Id);
        Assert.Equal(run?.Id, Assert.Single(channel.Published, published => published.Name == WorkflowsChannel.RunRecorded).Payload.Deserialize<WorkflowRun>(WorkflowsChannel.Json)?.Id);
        await host.Received(1).SetSessionStatusline("pane-2", "AC-1399");
    }

    private static JsonElement _Json(object request) => JsonSerializer.SerializeToElement(request, request.GetType(), WorkflowsChannel.Json);
}

using System.Text.Json;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Consent;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Shell;
using Cockpit.Core.Consent;
using Cockpit.Core.Projects;
using Cockpit.Infrastructure.Consent;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Consent;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Sessions;

// AC-128: set_status keys on the transport-verified pane, not the agent-named session, so no agent can spoof another's status.
public class SessionStatusToolsTests
{
    private static ISessionLabelSink _Labels()
    {
        var sink = Substitute.For<ISessionLabelSink>();
        sink.SetStatuslineAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        return sink;
    }

    private static SessionStatusTools _Tools(
        ISessionLabelSink? labels = null,
        ITrackedCommandRunner? runner = null,
        RunTracker? tracker = null,
        IWorkspaceAgentGateway? workspaces = null,
        IWorkspaceAgentCoordinator? coordinator = null,
        IAgentMessageInbox? inbox = null,
        IProjectJobHistory? jobHistory = null,
        IConsentBroker? consent = null,
        IElevatedCommandRunner? elevated = null) =>
        new(
            labels ?? _Labels(),
            runner ?? Substitute.For<ITrackedCommandRunner>(),
            tracker ?? new RunTracker(),
            workspaces ?? Substitute.For<IWorkspaceAgentGateway>(),
            coordinator ?? Substitute.For<IWorkspaceAgentCoordinator>(),
            inbox ?? Substitute.For<IAgentMessageInbox>(),
            jobHistory,
            consent,
            elevated);

    [Fact]
    public async Task SetStatus_KeysOnTheVerifiedPane_NotTheAgentSuppliedSessionId()
    {
        var sink = _Labels();
        var tools = _Tools(labels: sink);

        McpRequestContext.Set("verified-pane");
        try
        {
            // The agent spoofs another session's id in the tool argument.
            await tools.SetStatusAsync("pwned", "victim-pane");

            // The status lands on the verified caller, never the spoofed id.
            await sink.Received(1).SetStatuslineAsync("verified-pane", "pwned");
            await sink.DidNotReceive().SetStatuslineAsync("victim-pane", Arg.Any<string>());
        }
        finally
        {
            McpRequestContext.Set(null);
        }
    }

    // The name travels on the same tool call, so it inherits the same hazard: without this it would be a second way
    // to reach a session you do not own, and a renamed session is more disruptive than a rewritten status line.
    [Fact]
    public async Task SetStatus_ProposesTheNameToTheVerifiedPane_NotTheAgentSuppliedSessionId()
    {
        var sink = _Labels();
        sink.SuggestNameAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        var tools = _Tools(labels: sink);

        McpRequestContext.Set("verified-pane");
        try
        {
            await tools.SetStatusAsync("pwned", "victim-pane", "pwned-name");

            await sink.Received(1).SuggestNameAsync("verified-pane", "pwned-name");
            await sink.DidNotReceive().SuggestNameAsync("victim-pane", Arg.Any<string>());
        }
        finally
        {
            McpRequestContext.Set(null);
        }
    }

    // AC-1335 criterion 1, both halves on the same code: the card carries the literal command line the runner is
    // handed, the audit line carries the decision with that same text, and a denial starts nothing.
    [Theory]
    [InlineData(ConsentOutcome.Approved, ConsentAuditAction.Approved, 1, true)]
    [InlineData(ConsentOutcome.Denied, ConsentAuditAction.Denied, 0, false)]
    public async Task RunElevated_ShowsTheExactCommandLine_AndStartsOnlyWhatTheOperatorApproved(
        ConsentOutcome answer, ConsentAuditAction audited, int starts, bool ok)
    {
        var plan = new ElevatedCommandPlan(@"C:\ps\powershell.exe", @"-NoProfile -NonInteractive -Command ""& { Get-Date } *> 'C:\t\out.txt'""", @"C:\t\out.txt");
        var elevated = Substitute.For<IElevatedCommandRunner>();
        elevated.IsSupported.Returns(true);
        elevated.Plan("Get-Date").Returns(plan);
        elevated.RunAsync(plan, @"C:\work", Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ElevatedCommandResult(ElevatedCommandOutcome.Completed, 0, "Monday"));

        var entries = new List<ConsentAuditEntry>();
        var auditLog = Substitute.For<IConsentAuditLog>();
        auditLog.RecordAsync(Arg.Do<ConsentAuditEntry>(entries.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var broker = new ConsentService(auditLog);
        var prompts = new List<ConsentPrompt>();
        broker.PromptOpened += (_, prompt) =>
        {
            prompts.Add(prompt);
            broker.Respond(prompt.Id, answer, remember: false);
        };

        var tools = _Tools(consent: broker, elevated: elevated);
        McpRequestContext.Set("caller-pane");
        string reply;
        try
        {
            reply = await tools.RunElevatedAsync("Get-Date", @"C:\work", "to read the date as administrator");
        }
        finally
        {
            McpRequestContext.Set(null);
        }

        var prompt = Assert.Single(prompts);
        Assert.StartsWith(plan.CommandLine, prompt.Request.Action, StringComparison.Ordinal);
        Assert.Equal(ConsentRisk.Dangerous, prompt.Request.Risk);
        Assert.False(prompt.CanRemember);
        Assert.Equal(ConsentSourceCatalog.ElevatedCommand, prompt.Request.Source.Label);

        var entry = Assert.Single(entries);
        Assert.Equal(audited, entry.Action);
        Assert.StartsWith(plan.CommandLine, entry.ActionText, StringComparison.Ordinal);
        Assert.Equal("caller-pane", entry.PaneId);

        await elevated.Received(starts).RunAsync(plan, @"C:\work", Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        using var document = JsonDocument.Parse(reply);
        Assert.Equal(ok, document.RootElement.GetProperty("ok").GetBoolean());
    }
}

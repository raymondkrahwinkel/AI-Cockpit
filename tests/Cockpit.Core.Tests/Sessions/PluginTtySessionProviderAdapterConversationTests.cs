using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions.Tty;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.Sessions;

/// <summary>
/// <see cref="PluginTtySessionProviderAdapter"/>'s conversation-id wiring (AC-408) — the TTY route half of the one
/// seam both routes report a session's conversation id through. The adapter has no per-session pane context of
/// its own to construct with, so it reads the pane id <c>TtyLauncher</c> already put on the base environment as
/// <c>COCKPIT_PANE_ID</c> (AC-13) and wraps the sink into the callback the provider invokes.
/// </summary>
public class PluginTtySessionProviderAdapterConversationTests
{
    [Fact]
    public void BuildLaunch_WithASinkAndAPaneId_GivesTheProviderACallbackThatReportsToTheSink()
    {
        var inner = Substitute.For<IPluginTtyProvider>();
        PluginTtyLaunchContext? captured = null;
        inner.BuildLaunch(Arg.Do<PluginTtyLaunchContext>(context => captured = context))
            .Returns(new PluginTtyLaunchSpec("claude", [], new Dictionary<string, string?>(), "/wd", []));
        var sink = Substitute.For<ISessionConversationSink>();
        var adapter = new PluginTtySessionProviderAdapter("claude-provider", inner, "{}", conversationSink: sink);

        var context = new TtyLaunchContext(
            null, new Dictionary<string, string>(), "/wd", null,
            new Dictionary<string, string> { ["COCKPIT_PANE_ID"] = "pane-1" });

        adapter.BuildLaunch(context);

        Assert.NotNull(captured?.ReportConversationId);
        captured!.ReportConversationId!(PluginConversationId.Known("session-a"));
        sink.Received(1).Report("pane-1", SessionConversationId.Known("session-a"));
    }

    [Fact]
    public void BuildLaunch_WithNoSinkWired_GivesTheProviderNoCallback()
    {
        var inner = Substitute.For<IPluginTtyProvider>();
        PluginTtyLaunchContext? captured = null;
        inner.BuildLaunch(Arg.Do<PluginTtyLaunchContext>(context => captured = context))
            .Returns(new PluginTtyLaunchSpec("claude", [], new Dictionary<string, string?>(), "/wd", []));
        var adapter = new PluginTtySessionProviderAdapter("claude-provider", inner, "{}");

        var context = new TtyLaunchContext(
            null, new Dictionary<string, string>(), "/wd", null,
            new Dictionary<string, string> { ["COCKPIT_PANE_ID"] = "pane-1" });

        adapter.BuildLaunch(context);

        Assert.Null(captured?.ReportConversationId);
    }

    [Fact]
    public void BuildLaunch_WithNoPaneIdOnTheEnvironment_GivesTheProviderNoCallback_EvenWithASink()
    {
        var inner = Substitute.For<IPluginTtyProvider>();
        PluginTtyLaunchContext? captured = null;
        inner.BuildLaunch(Arg.Do<PluginTtyLaunchContext>(context => captured = context))
            .Returns(new PluginTtyLaunchSpec("claude", [], new Dictionary<string, string?>(), "/wd", []));
        var sink = Substitute.For<ISessionConversationSink>();
        var adapter = new PluginTtySessionProviderAdapter("claude-provider", inner, "{}", conversationSink: sink);

        var context = new TtyLaunchContext(null, new Dictionary<string, string>(), "/wd", null, new Dictionary<string, string>());

        adapter.BuildLaunch(context);

        Assert.Null(captured?.ReportConversationId);
    }
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Workspaces;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Backend.Tests.Plugins;

// AC-1392: the host a plugin's backend part gets without a window, on the F1 contracts. Every session call here runs
// on a threadpool thread, which is what a node call is: no SynchronizationContext to marshal back onto (AC-1381).
public class PluginBackendHostTests
{
    private const string PaneId = "pane-1";

    public static TheoryData<string, Func<ICockpitHost, Task>, Action<ISessionHandle>> SessionMutations => new()
    {
        { "SendToSessionAsync", host => host.SendToSessionAsync(PaneId, "draw a box"), pane => pane.Received(1).InjectAndSubmitAsync("draw a box") },
        { "SetSessionName", host => host.SetSessionName(PaneId, "AC-1392"), pane => pane.Received(1).SetNameAsync("AC-1392") },
        { "SuggestSessionName", host => host.SuggestSessionName(PaneId, "AC-1392"), pane => pane.Received(1).SuggestNameAsync("AC-1392") },
        { "SetSessionStatusline", host => host.SetSessionStatusline(PaneId, "reviewing"), pane => pane.Received(1).SetStatuslineAsync("reviewing") },
    };

    // Acceptance 2: decided under the launcher's exclusion, and the pane it found is the one acted on.
    [Theory]
    [MemberData(nameof(SessionMutations))]
    public async Task ASessionMutation_FromAThreadWithoutASynchronizationContext_ReachesThePane(
        string member, Func<ICockpitHost, Task> mutate, Action<ISessionHandle> reached)
    {
        var registry = new SessionRegistry();
        var launcher = _InlineLauncher();
        var pane = _Pane(PaneId, "Echo");
        registry.Register(pane);
        var host = _Host(registry, launcher);

        await Task.Run(() => mutate(host));

        Assert.NotEmpty(member);
        await launcher.Received(1).RunExclusiveAsync(Arg.Any<Func<ISessionHandle?>>());
        reached(pane);
    }

    // Acceptance 2's counter-proof: the pane closes on another thread after the decision and before the action. The
    // re-check refuses the send instead of writing to a pane that is gone.
    [Fact]
    public async Task APaneThatClosesBetweenTheDecisionAndTheAction_IsLeftAlone()
    {
        var registry = new SessionRegistry();
        var pane = _Pane(PaneId, "Echo");
        registry.Register(pane);
        var launcher = Substitute.For<ISessionLauncher>();
        launcher.RunExclusiveAsync(Arg.Any<Func<ISessionHandle?>>())
            .Returns(call => _DecideThenCloseAsync(call.Arg<Func<ISessionHandle?>>(), registry));
        var host = _Host(registry, launcher);

        await Task.Run(() => host.SendToSessionAsync(PaneId, "anyone there?"));

        await launcher.Received(1).RunExclusiveAsync(Arg.Any<Func<ISessionHandle?>>());
        await pane.DidNotReceive().InjectAndSubmitAsync(Arg.Any<string>());
    }

    // A binding made off any UI thread reads the registry each time, sends through the host, and ends with the pane.
    [Fact]
    public async Task BindToSession_OffTheUiThread_SendsWhileLive_AndEndsWhenThePaneCloses()
    {
        var registry = new SessionRegistry();
        var pane = _Pane(PaneId, "Echo");
        registry.Register(pane);
        var host = _Host(registry, _InlineLauncher());

        using var binding = await Task.Run(() => host.BindToSession(PaneId));
        var ended = 0;
        binding.Ended += (_, _) => ended++;
        var before = (binding.IsLive, binding.SessionName);
        await Task.Run(() => binding.SendAsync("pin this"));
        registry.Unregister(PaneId);

        Assert.Equal((true, "Echo"), before);
        Assert.Equal((false, (string?)null, 1), (binding.IsLive, binding.SessionName, ended));
        await pane.Received(1).InjectAndSubmitAsync("pin this");
    }

    // D6: the backend observer knows the open panes and which one closed, and has no selection to report.
    [Fact]
    public void TheBackendObserver_ListsTheOpenPanes_ReportsEachClosedOneOnce_AndHasNoActiveSession()
    {
        var registry = new SessionRegistry();
        registry.Register(_Pane("pane-1", "One"));
        registry.Register(_Pane("pane-2", "Two"));
        ICockpitSessionObserver observer = new PluginBackendSessionObserver(registry);
        var closed = new List<string>();
        observer.SessionClosed += (_, paneId) => closed.Add(paneId);

        registry.Unregister("pane-1");
        registry.Register(_Pane("pane-3", "Three"));

        Assert.Equal([new OpenCockpitSession("pane-2", "Two"), new OpenCockpitSession("pane-3", "Three")], observer.OpenSessions);
        Assert.Equal(["pane-1"], closed);
        Assert.Equal(((string?)null, (string?)null), (observer.ActivePaneId, observer.ActiveSessionWorkingDirectory));
    }

    // The window members do nothing and say so once per plugin; the new-session dialog still keeps its
    // exactly-one-callback promise, as a cancel.
    [Fact]
    public async Task TheWindowMembers_AreNoOps_ThatLogOncePerPlugin()
    {
        var lines = new List<string>();
        using var logs = LoggerFactory.Create(builder => builder.AddProvider(new CollectingLoggerProvider(lines)));
        var host = _Host(new SessionRegistry(), _InlineLauncher(), logs);
        var cancelled = 0;

        host.AddSideMenuButton("Open", () => { });
        host.AddSideMenuButtonWithBadge("Issues", () => { });
        await host.OpenWorkspaceAsync("workspace.example");
        await host.ShowNewSessionDialogAsync(onStarted: _ => Assert.Fail("Nothing can start without a dialog."), onCancelled: () => cancelled++);

        Assert.Equal(1, cancelled);
        Assert.Equal(["Plugin diagram called AddSideMenuButton, and this backend has no window: its window contributions are ignored."], lines);
    }

    // AC-1369 condition (a): the host is built and used here, in the suite that guards the no-plugin backend, and not
    // one Avalonia assembly comes with it — the no-op defaults on ICockpitHost are what keeps it that way.
    [Fact]
    public async Task TheBackendHost_LoadsNoAvaloniaAssembly()
    {
        var registry = new SessionRegistry();
        registry.Register(_Pane(PaneId, "Echo"));
        var host = _Host(registry, _InlineLauncher());

        host.AddSideMenuButton("Open", () => { });
        await host.SendToSessionAsync(PaneId, "hello");

        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), assembly => assembly.GetName().Name?.StartsWith("Avalonia", StringComparison.Ordinal) == true);
    }

    // D6 for the actions: no selection, clipboard or operator, so a confirmation is refused, never assumed; starting a
    // session goes through the launcher onto the first Sessions desk.
    [Fact]
    public async Task TheBackendActions_RefuseAConfirmation_AndStartASessionOnTheFirstSessionsDesk()
    {
        var desk = Workspace.Create("Sessions", WorkspaceType.Sessions);
        var launcher = Substitute.For<ISessionLauncher>();
        launcher.Workspaces.Returns(new WorkspaceSettings { Workspaces = [Workspace.Create("Projects", WorkspaceType.Projects), desk] });
        launcher.RunExclusiveAsync(Arg.Any<Func<string?>>()).Returns(call => Task.FromResult(call.Arg<Func<string?>>()()));
        launcher.StartSessionAsync(Arg.Any<SessionLaunchRequest>()).Returns(new LaunchedSession("pane-9", "Echo 1", PromptDelivered: true));
        var profiles = Substitute.For<ISessionProfileStore>();
        profiles.LoadAsync(Arg.Any<CancellationToken>()).Returns([new SessionProfile("Echo", new ClaudeConfig("/fake/.claude"))]);
        ICockpitActions actions = new PluginBackendActions(profiles, Substitute.For<IDelegationService>(), launcher);

        var name = await Task.Run(() => actions.StartSessionAsync("echo", "hello"));

        Assert.Equal(("Echo 1", false, false), (name, await actions.ConfirmAsync("Delete", "Sure?"), actions.HasActiveSession));
        await launcher.Received(1).StartSessionAsync(Arg.Is<SessionLaunchRequest>(request => request.WorkspaceId == desk.Id && request.Prompt == "hello"));
    }

    private static PluginBackendHost _Host(SessionRegistry registry, ISessionLauncher launcher, ILoggerFactory? logs = null)
    {
        var services = new ServiceCollection()
            .AddSingleton<ISessionRegistry>(registry)
            .AddSingleton(launcher)
            .AddSingleton(logs ?? LoggerFactory.Create(_ => { }))
            .BuildServiceProvider();

        return new PluginBackendHost(
            "diagram",
            "Diagram",
            services,
            new PluginStorage(new Dictionary<string, string>(), _ => { }),
            new PluginBackendSessionObserver(registry),
            new PluginBackendActions(Substitute.For<ISessionProfileStore>(), Substitute.For<IDelegationService>()),
            new PluginDiagnostics());
    }

    private static ISessionLauncher _InlineLauncher()
    {
        var launcher = Substitute.For<ISessionLauncher>();
        launcher.RunExclusiveAsync(Arg.Any<Func<ISessionHandle?>>())
            .Returns(call => Task.FromResult(call.Arg<Func<ISessionHandle?>>()()));
        return launcher;
    }

    private static async Task<ISessionHandle?> _DecideThenCloseAsync(Func<ISessionHandle?> decision, SessionRegistry registry)
    {
        var decided = decision();
        await Task.Run(() => registry.Unregister(PaneId));
        return decided;
    }

    private static ISessionHandle _Pane(string paneId, string title)
    {
        var pane = Substitute.For<ISessionHandle>();
        pane.PaneId.Returns(paneId);
        pane.Title.Returns(title);
        return pane;
    }

    private sealed class CollectingLoggerProvider(List<string> lines) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CollectingLogger(lines);

        public void Dispose()
        {
        }
    }

    private sealed class CollectingLogger(List<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (lines)
            {
                lines.Add(formatter(state, exception));
            }
        }
    }
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Core.Workspaces;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.StatusBar;

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
        { "InsertIntoSessionAsync", host => host.InsertIntoSessionAsync(PaneId, "draw a box"), pane => pane.Received(1).InsertTextAsync("draw a box") },
        { "SetSessionStatuslineAsync", host => host.SetSessionStatuslineAsync(PaneId, "reviewing"), pane => pane.Received(1).SetStatuslineAsync("reviewing") },
        { "SetSessionNameAsync", host => host.SetSessionNameAsync(PaneId, "AC-1419"), pane => pane.Received(1).SetNameAsync("AC-1419") },
    };

    // The two members and the handle call each one lands on, so one theory covers both.
    public static TheoryData<Func<ICockpitHost, string, Task<bool>>, Action<ISessionHandle, bool>> AnsweringMembers => new()
    {
        { (host, paneId) => host.SetSessionStatuslineAsync(paneId, "reviewing"), (pane, takes) => pane.SetStatuslineAsync(Arg.Any<string>()).Returns(takes) },
        { (host, paneId) => host.SetSessionNameAsync(paneId, "AC-1419"), (pane, takes) => pane.SetNameAsync(Arg.Any<string>()).Returns(takes) },
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

    // AC-1399: placing text answers whether a live pane took it — false for a pane that is not there, and false for one
    // whose handle has no input (headless), so a workflow step can fail visibly instead of doing nothing.
    [Theory]
    [InlineData(PaneId, true, true)]
    [InlineData(PaneId, false, false)]
    [InlineData("pane-9", true, false)]
    public async Task InsertIntoSessionAsync_AnswersWhetherALivePaneTookTheText(string paneId, bool paneTakesText, bool expected)
    {
        var registry = new SessionRegistry();
        var pane = _Pane(PaneId, "Echo");
        pane.InsertTextAsync(Arg.Any<string>()).Returns(paneTakesText);
        registry.Register(pane);
        var host = _Host(registry, _InlineLauncher());

        var answer = await Task.Run(() => host.InsertIntoSessionAsync(paneId, "draw a box"));

        Assert.Equal(expected, answer);
    }

    // AC-1419: a label or a name answers whether a live pane took it — false for an unknown pane and for a handle that
    // refuses, so a workflow step can fail visibly.
    [Theory]
    [MemberData(nameof(AnsweringMembers))]
    public async Task SetSessionStatuslineAsync_AndNameAsync_AnswerWhetherALivePaneTookIt(
        Func<ICockpitHost, string, Task<bool>> ask, Action<ISessionHandle, bool> configure)
    {
        var registry = new SessionRegistry();
        var live = _Pane(PaneId, "Echo");
        configure(live, true);
        registry.Register(live);
        var refusing = _Pane("pane-2", "Echo");
        configure(refusing, false);
        registry.Register(refusing);
        var host = _Host(registry, _InlineLauncher());

        var took = await Task.Run(() => ask(host, PaneId));
        var refused = await Task.Run(() => ask(host, "pane-2"));
        var unknown = await Task.Run(() => ask(host, "pane-9"));

        Assert.True(took);
        Assert.False(refused);
        Assert.False(unknown);
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

    // D6: the backend observer knows the open panes and which one closed, and has no selection to report — but it
    // does know where each open pane works, by its id (AC-1397), and nothing for one that closed.
    [Fact]
    public void TheBackendObserver_ListsTheOpenPanes_ReportsEachClosedOneOnce_AndHasNoActiveSession()
    {
        var registry = new SessionRegistry();
        var one = _Pane("pane-1", "One");
        one.WorkingDirectory.Returns("/work/one");
        var two = _Pane("pane-2", "Two");
        two.WorkingDirectory.Returns("/work/two");
        registry.Register(one);
        registry.Register(two);
        ICockpitSessionObserver observer = new PluginBackendSessionObserver(registry);
        var closed = new List<string>();
        observer.SessionClosed += (_, paneId) => closed.Add(paneId);

        registry.Unregister("pane-1");
        registry.Register(_Pane("pane-3", "Three"));

        Assert.Equal([new OpenCockpitSession("pane-2", "Two"), new OpenCockpitSession("pane-3", "Three")], observer.OpenSessions);
        Assert.Equal(["pane-1"], closed);
        Assert.Equal(("/work/two", (string?)null), (observer.GetWorkingDirectory("pane-2"), observer.GetWorkingDirectory("pane-1")));
    }

    // AC-1415: a plugin in a backend without an App hears what a real session produces, through its handle: the reply,
    // the tool call with its result, and the turn's images, which go when the turn ends.
    [Fact]
    public async Task TheBackendObserver_RelaysAHeadlessSessionsOutput_ToolCalls_AndTurnImages()
    {
        var runtime = Substitute.For<ISessionRuntime>();
        runtime.IsRunning.Returns(true);
        var manager = Substitute.For<ISessionManager>();
        manager.Create(Arg.Any<SessionProfile?>()).Returns(runtime);
        var host = new SessionHost<QueuedPrompt>(() => PaneId, manager, TimeProvider.System);
        host.Attach(new SessionProfile("Echo", new ClaudeConfig("/fake/.claude")));
        var registry = new SessionRegistry();
        registry.Register(new SessionHostHandle(PaneId, "Echo", nameIsChosen: false, "desk", "/work/echo", "Echo", host));
        ICockpitSessionObserver observer = new PluginBackendSessionObserver(registry);
        var output = new List<SessionOutputText>();
        var tools = new List<SessionToolActivity>();
        observer.OutputProduced += (_, text) => output.Add(text);
        observer.ToolActivityObserved += (_, call) => tools.Add(call);

        await Task.Run(() =>
        {
            var session = Assert.IsType<SessionHostHandle>(registry.Find(PaneId));
            session.AddPastedImage([1, 2, 3]);
            session.InjectAndSubmit("hello");
            _Raise(runtime, new ToolUseRequested { SessionId = "S1", ToolUseId = "t1", ToolName = "Bash", InputJson = "{}" });
            _Raise(runtime, new ToolResult { SessionId = "S1", ToolUseId = "t1", Content = "ok", IsError = false });
            _Raise(runtime, new AssistantTextCompleted { SessionId = "S1", Text = "echo: hello" });
            Assert.Equal(["pasted-image-1.png"], observer.GetCurrentTurnImages(PaneId).Select(image => image.SuggestedFileName));
            _Raise(runtime, new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "echo: hello", IsError = false });
        });

        Assert.Equal([new SessionOutputText("ok", "/work/echo", false), new SessionOutputText("echo: hello", "/work/echo", false)], output);
        Assert.Equal([new SessionToolActivity(PaneId, "Bash", "{}", "ok", false)], tools);
        Assert.Empty(observer.GetCurrentTurnImages(PaneId));
    }

    // What a backend has no window for does nothing and says so once per plugin.
    [Fact]
    public void AWindowlessContribution_IsANoOp_ThatLogsOncePerPlugin()
    {
        var lines = new List<string>();
        using var logs = LoggerFactory.Create(builder => builder.AddProvider(new CollectingLoggerProvider(lines)));
        var host = _Host(new SessionRegistry(), _InlineLauncher(), logs);

        host.AddSupervisedActivityProvider(Substitute.For<ISupervisedActivitySource>());
        host.AddSupervisedActivityProvider(Substitute.For<ISupervisedActivitySource>());

        Assert.Equal(["Plugin diagram called AddSupervisedActivityProvider, and this backend has no window: its window contributions are ignored."], lines);
    }

    // AC-1369 condition (a): the host is built and used here, in the suite that guards the no-plugin backend, and not
    // one Avalonia assembly comes with it.
    [Fact]
    public async Task TheBackendHost_LoadsNoAvaloniaAssembly()
    {
        var registry = new SessionRegistry();
        registry.Register(_Pane(PaneId, "Echo"));
        var host = _Host(registry, _InlineLauncher());

        await host.SendToSessionAsync(PaneId, "hello");

        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), assembly => assembly.GetName().Name?.StartsWith("Avalonia", StringComparison.Ordinal) == true);
    }

    // Starting a session goes through the launcher onto the first Sessions desk.
    [Fact]
    public async Task TheBackendActions_StartASessionOnTheFirstSessionsDesk()
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

        Assert.Equal("Echo 1", name);
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

    private static void _Raise(ISessionRuntime runtime, SessionEvent sessionEvent) =>
        runtime.EventAppended += Raise.Event<Action<SessionEvent>>(sessionEvent);

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

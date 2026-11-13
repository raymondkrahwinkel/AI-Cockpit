using System.Text.Json.Nodes;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Infrastructure.BackendApi;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Projects;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Sessions.Tty;
using Cockpit.Plugins.Abstractions.Projects;
using Cockpit.Plugins.Abstractions.Profiles;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Backend.Tests.Plugins;

// AC-1392: the host a plugin's backend part gets without a window, on the F1 contracts. Every session call here runs
// on a threadpool thread, which is what a node call is: no SynchronizationContext to marshal back onto (AC-1381).
public class PluginBackendHostTests
{
    private const string PaneId = "pane-1";

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

        // Category A: a task is never an answer to a CLI question or SDK permission, on either send overload.
        registry.Register(pane);
        var liveHost = _Host(registry, _InlineLauncher());
        pane.InjectAndSubmitAsync(Arg.Any<string>()).Returns(true);
        pane.AcceptsImages.Returns(true);
        pane.SubmitWithImagesAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<byte[]>>()).Returns(true);
        pane.ReadPendingPermissionsAsync().Returns(Task.FromResult<IReadOnlyList<SessionPendingPermission>>([]));
        pane.SessionStatus.Returns(SessionStatus.NeedsAttention);
        await liveHost.SendToSessionAsync(PaneId, "2");
        Assert.False((await liveHost.SendToSessionAsync(PaneId, "2", [])).Delivered);
        pane.SessionStatus.Returns(SessionStatus.Idle);
        pane.HasPendingConsent.Returns(true);
        Assert.False((await liveHost.SendToSessionAsync(PaneId, "2", [])).Delivered);
        pane.HasPendingConsent.Returns(false);
        pane.ReadPendingPermissionsAsync().Returns(Task.FromResult<IReadOnlyList<SessionPendingPermission>>(
            [new SessionPendingPermission("permission", "tool", "{}", DateTimeOffset.UnixEpoch)]));
        Assert.False((await liveHost.SendToSessionAsync(PaneId, "2", [])).Delivered);
        await pane.DidNotReceive().InjectAndSubmitAsync(Arg.Any<string>());
        await pane.DidNotReceive().SubmitWithImagesAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<byte[]>>());
        pane.ReadPendingPermissionsAsync().Returns(Task.FromResult<IReadOnlyList<SessionPendingPermission>>([]));
        Assert.True((await liveHost.SendToSessionAsync(PaneId, "a task", [])).Delivered);
        await pane.Received(1).InjectAndSubmitAsync("a task");
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
        two.ActiveProfileLabel.Returns("work");
        two.SessionStatus.Returns(SessionStatus.NeedsAttention);
        registry.Register(one);
        registry.Register(two);
        ICockpitSessionObserver observer = new PluginBackendSessionObserver(registry);
        var closed = new List<string>();
        observer.SessionClosed += (_, paneId) => closed.Add(paneId);

        registry.Unregister("pane-1");
        registry.Register(_Pane("pane-3", "Three"));

        Assert.Equal(("work", "NeedsAttention"), (observer.OpenSessions[0].ProfileLabel, observer.OpenSessions[0].Status));
        Assert.Equal(["pane-1"], closed);
        Assert.Equal(("/work/two", (string?)null), (observer.GetWorkingDirectory("pane-2"), observer.GetWorkingDirectory("pane-1")));
        registry.RegisterAssistant(_Pane("cockpit-assistant", "Old assistant"));
        registry.RegisterAssistant(_Pane("cockpit-assistant", "New assistant"));
        registry.Register(_Pane("pane-2", "Replacement"));
        Assert.Equal(["pane-1", "cockpit-assistant", "pane-2"], closed);
        var keyring = new SessionMcpKeyring();
        using var services = new ServiceCollection().AddSingleton(keyring).BuildServiceProvider();
        var lifecycle = new PluginBackendSessionObserver(registry, services);
        var ended = new List<string>();
        lifecycle.SessionClosed += (_, paneId) => ended.Add(paneId);
        var oldToken = keyring.TokenFor("pane-2");
        var newToken = keyring.TokenFor("pane-2");
        Assert.Null(keyring.PaneFor(oldToken));
        Assert.Equal(["pane-2"], ended);
        keyring.Revoke("pane-2", oldToken);
        Assert.Single(ended);
        Assert.Equal("pane-2", keyring.PaneFor(newToken));
        keyring.Revoke("pane-2", newToken);
        Assert.Null(keyring.PaneFor(newToken));
        Assert.Equal(["pane-2", "pane-2"], ended);
    }

    // Layer 2, credential policy: only confirmed API keys or local models suppress the personal-login hint.
    [Fact]
    public async Task ProfileHints_UseTheFailClosedCredentialPolicy_LocallyAndOverTheApi()
    {
        var personal = new SessionProfile("personal", new PluginProviderConfig("echo", "{}"));
        var ollama = new SessionProfile("ollama", new OllamaConfig("http://localhost:11434", "local"));
        var studio = new SessionProfile("studio", new LmStudioConfig("http://localhost:1234", "local"));
        var store = Substitute.For<ISessionProfileStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<SessionProfile>>([personal, ollama, studio]));
        var logins = Substitute.For<IProfileLoginChecker>();
        var loginStarter = Substitute.For<IProfileLoginStarter>();
        loginStarter.CanStartLogin(personal).Returns(true);
        Assert.Equal(ProfileSignInKind.NotSignedIn, ProfileEndpoints.ToWire(personal, [], [], loginStarter: loginStarter).SignIn);
        Assert.Null(ProfileEndpoints.ToWire(ollama, [], [], loginStarter: loginStarter).SignIn);
        Assert.Equal(ProfileSignInKind.SignedIn, ProfileEndpoints.ToWire(personal,
            [new ProfileLoginHealth(personal.Label, true, DateTimeOffset.UtcNow, null) { SignIn = ProfileSignInKind.SignedIn }], [], loginStarter: loginStarter).SignIn);
        var host = _Host(new SessionRegistry(), _InlineLauncher(), services => services
            .AddSingleton(store).AddSingleton(logins));

        foreach (var (credential, expected) in new[]
        {
            (ProfileCredentialKind.Unknown, true),
            (ProfileCredentialKind.Login, true),
            (ProfileCredentialKind.RenewingLogin, true),
            (ProfileCredentialKind.ApiKey, false),
            (ProfileCredentialKind.ApiKeyFromSecret, false),
        })
        {
            logins.CredentialKind(Arg.Any<SessionProfile>()).Returns(credential);
            var profiles = await host.GetProfilesAsync();
            Assert.Equal(new[] { expected, false, false }, profiles.Select(profile => profile.UsesPersonalLogin));
            Assert.Equal(expected, ProfileEndpoints.ToWire(personal, [], [], logins: logins).UsesPersonalLogin);
            Assert.False(ProfileEndpoints.ToWire(ollama, [], [], logins: logins).UsesPersonalLogin);
            Assert.False(ProfileEndpoints.ToWire(studio, [], [], logins: logins).UsesPersonalLogin);
        }

        var withoutChecker = _Host(new SessionRegistry(), _InlineLauncher(), services => services.AddSingleton(store));
        Assert.True((await withoutChecker.GetProfilesAsync())[0].UsesPersonalLogin);
        Assert.True(new PluginProfileInfo("unknown", "Plugin", "").UsesPersonalLogin);
        var oldWire = JsonSerializer.SerializeToNode(ProfileEndpoints.ToWire(personal, [], []))?.AsObject()
            ?? throw new InvalidOperationException("The profile was not serialized.");
        oldWire.Remove(nameof(RemoteProfile.UsesPersonalLogin));
        Assert.True(oldWire.Deserialize<RemoteProfile>()?.UsesPersonalLogin);

        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), assembly => assembly.GetName().Name?.StartsWith("Avalonia", StringComparison.Ordinal) == true);
    }

    // AC-1520: the rewrite touches only references under the plugin's own scheme, leaves the rest of cockpit.json as it was, and a second pass changes nothing.
    // AC-1737: on the same store, the reads list every project (neither has a folder) by id and name, and resolve a pane's project.
    [Fact]
    public async Task ProjectReads_ListEveryProjectAndAPanesProject_AndTheRewriteChangesOnlyTheNamedSchemesReferences()
    {
        var path = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"), "cockpit.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, """
            {"Unrelated":{"keep":[1,2]},"Projects":[
              {"Id":"a","Name":"A","Resources":[{"Reference":"depot:cockpit","Role":"Memory"},{"Reference":"depot:cockpit/notes.md","Role":"Reference","Label":"Notes"},{"Reference":"jira:cockpit","Role":"Reference"},{"Reference":"depot.other:cockpit","Role":"Reference"},{"Reference":"depot:wispslate/other","Role":"Reference"}]},
              {"Id":"b","Name":"B","Resources":[{"Reference":"/work/b","Role":"Memory"}]}]}
            """);
        var store = new ProjectStore(path);
        await store.SaveAsync(await store.LoadAsync());
        var before = await File.ReadAllTextAsync(path);
        var catalog = new ProjectCatalog(store, new ProjectOwnershipRegistry(), new SharedProjectSourceRegistry());
        var registry = new SessionRegistry();
        var pane = _Pane(PaneId, "Echo");
        pane.ProjectId.Returns("b");
        registry.Register(pane);
        registry.Register(_Pane("pane-2", "Echo"));
        var host = _Host(registry, _InlineLauncher(), services => services.AddSingleton<IProjectStore>(store).AddSingleton<IProjectEditor>(catalog));

        Assert.Equal([new PluginProjectInfo("a", "A"), new PluginProjectInfo("b", "B")], await host.GetProjectsAsync());
        Assert.Equal("b", await host.GetSessionProjectIdAsync(PaneId, CancellationToken.None));
        Assert.Null(await host.GetSessionProjectIdAsync("pane-2", CancellationToken.None));
        Assert.Null(await host.GetSessionProjectIdAsync("pane-9", CancellationToken.None));

        Func<string, string?> rewrite = value => value.StartsWith("cockpit", StringComparison.Ordinal) ? "wispslate/" + value : null;

        var first = await host.RewriteProjectReferencesAsync("depot", rewrite);
        var after = await File.ReadAllTextAsync(path);
        var second = await host.RewriteProjectReferencesAsync("depot", rewrite);

        Assert.Equal(2, first);
        var expected = before.Replace("\"depot:cockpit\"", "\"depot:wispslate/cockpit\"").Replace("depot:cockpit/notes.md", "depot:wispslate/cockpit/notes.md");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(after)), after);
        Assert.Equal(0, second);
        Assert.Equal(after, await File.ReadAllTextAsync(path));
    }

    private static PluginBackendHost _Host(
        SessionRegistry registry, ISessionLauncher launcher, ILoggerFactory? logs = null, IPluginProviderRegistry? providers = null, IPluginTtyProviderRegistry? ttyProviders = null)
        => _Host(registry, launcher, null, logs, providers, ttyProviders);

    private static PluginBackendHost _Host(
        SessionRegistry registry, ISessionLauncher launcher, Action<IServiceCollection>? configure, ILoggerFactory? logs = null, IPluginProviderRegistry? providers = null, IPluginTtyProviderRegistry? ttyProviders = null)
    {
        var collection = new ServiceCollection();
        configure?.Invoke(collection);
        var services = collection
            .AddSingleton<ISessionRegistry>(registry)
            .AddSingleton(launcher)
            .AddSingleton(logs ?? LoggerFactory.Create(_ => { }))
            .AddSingleton(providers ?? new PluginProviderRegistry())
            .AddSingleton(ttyProviders ?? new PluginTtyProviderRegistry())
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

}

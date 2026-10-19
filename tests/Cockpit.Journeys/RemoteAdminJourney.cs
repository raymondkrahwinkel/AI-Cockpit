using System.Diagnostics;
using System.Net;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.Plugins;
using Cockpit.App.ViewTests;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Core.Plugins;
using Cockpit.Core.Projects;
using Cockpit.Infrastructure.BackendApi;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Infrastructure.Projects;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.TestSupport;

namespace Cockpit.Journeys;

// J10 (AC-1446), Admin on a server: the desktop holds an admin key to Cockpit.Server and opens Options there. F5.6b2
// added the projects step; b3–b4 add profiles and plugins to this route.
[Collection(JourneyCollection.Alone)]
public sealed class RemoteAdminJourney
{
    private const string Server = "journey-server";

    private const string Laptop = "journey-laptop";

    private const string Phone = "journey-phone";

    private const string Project = "journey-project";

    private const string StorePlugin = "echo-provider";

    // AC-1514: the echo fixture's one shared definition (Cockpit.Plugin.EchoProvider's EchoSharedProjectSource).
    private const string SharedProject = "echo-shared:journey";
    private const string BareProject = "echo-shared:bare";

    private const string SharedName = "journey-shared";

    // Admin → Connect keys → issue; the new key connects; revoke; the revoked key is refused; a locked-out address is
    // lifted. AC-1472: Projects clones a bare repository; it is in GET /projects and "+ Start on", and a session runs in
    // it. Each step is in the Audit log with the key that called as its actor, never the node tools' identity.
    [Fact]
    public async Task AnAdminKey_IssuesAKeyThatConnects_RevokesIt_AndLiftsALockout_EachInTheAuditUnderItsOwnLabel()
    {
        var root = Directory.CreateTempSubdirectory("journey-admin-").FullName;
        var stateRoot = Path.Combine(root, "state");
        var store = _CreateFixtureStore(root);
        var bootstrap = _NewKey();
        var origin = Path.Combine(root, "origin.git");
        var work = Path.Combine(root, "origin-work");
        await _GitAsync(root, "init", "--bare", "-b", "main", origin);
        await _GitAsync(root, "init", "-b", "main", work);
        await File.WriteAllTextAsync(Path.Combine(work, "README.md"), "journey");
        await _GitAsync(work, "add", "README.md");
        await _GitAsync(work, "-c", "user.name=journey", "-c", "user.email=journey@example.invalid", "commit", "-m", "first");
        await _GitAsync(work, "push", origin, "main");
        // AC-1514: the repository the echo fixture's shared definition names, its own so the clone above does not claim it.
        var shared = Path.Combine(root, "shared.git");
        await _GitAsync(root, "clone", "--bare", origin, shared);
        // AC-1525: one door port across the restart, as a container keeps its own. The door listens on every address one
        // above the pairing port, so that is the port found free.
        var port = _FreeDoorPort();
        var (fingerprint, _, _) = await ServerJourney._PrepareStateRootAsync(stateRoot, port - NodeEndpointSettings.McpPortOffset, root, "http://127.0.0.1:9/webhook", pluginStore: store, withEchoPlugin: false);
        var keyFile = ServerJourney._Secret(root, "connect-key", bootstrap);
        ServerJourney.ServerRun startServer() => ServerJourney._RunServer(
            ServerJourney._Metadata("CockpitServerDirectory"), stateRoot, keyFile,
            new Dictionary<string, string> { ["COCKPIT_ALLOW_FILE_CLONES"] = "1", ["COCKPIT_ECHO_SHARED_REPOSITORY"] = new Uri(shared).AbsoluteUri });
        var run = startServer();
        try
        {
            await run.Running.WaitAsync(Until.Ceiling);
            var baseAddress = new Uri($"https://127.0.0.1:{port}/");
            using var setup = new BackendApiClient(baseAddress, bootstrap, fingerprint, TimeProvider.System);
            var laptop = await setup.SendAsync<JsonObject>(HttpMethod.Post, "api/v1/keys", new { label = Laptop, capability = "admin" });
            var laptopKey = laptop["secret"]?.GetValue<string>() ?? throw new InvalidOperationException("The server issued no key.");

            await using var cockpit = JourneyHost.Desktop();
            await cockpit.Services.GetRequiredService<IMcpServerStore>().SaveAsync(
            [
                new McpServerConfig
                {
                    Id = McpServerIdentity.NewId(),
                    Name = NodeServerName.For(Server, NodeServerName.SessionsServerName),
                    Transport = McpTransport.Http,
                    Scope = McpServerScope.LocalOnly,
                    Url = $"https://127.0.0.1:{port}/mcp",
                    Auth = McpServerAuth.ApiKey,
                    ApiKey = laptopKey,
                    PinnedCertificateFingerprint = fingerprint,
                },
            ]);
            await cockpit.StartDesktopAsync();

            var opened = new TaskCompletionSource<OptionsDialog>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var watch = Window.WindowOpenedEvent.AddClassHandler<OptionsDialog>((dialog, _) => opened.TrySetResult(dialog));
            var view = cockpit.Cockpit;
            var adminClosed = Task.CompletedTask;
            await HeadlessAvalonia.RunAsync(async () =>
            {
                await Until.CollectionHolds(view.ServerGroups, () => view.ServerGroups.Count == 1);
                var group = view.ServerGroups[0];
                await Until.Holds(group, () => group.IsAdminKey);
                adminClosed = view.OpenServerAdminCommand.ExecuteAsync(group);
            });
            var dialog = await opened.Task.WaitAsync(Until.Ceiling);
            var admin = dialog.Server ?? throw new InvalidOperationException("Admin opened Options without its server.");

            await HeadlessAvalonia.RunAsync(async () =>
            {
                dialog.SelectCategory("server-plugins");
                await Until.LayoutHolds(dialog, () => dialog.GetVisualDescendants().OfType<ServerPluginsPage>().Count() == 1);
            });
            await HeadlessAvalonia.RunAsync(() => admin.LoadCommand.ExecuteAsync(null));
            var localPluginIds = view.Plugins.Plugins.Select(plugin => plugin.FolderId).ToArray();
            var storeOpened = new TaskCompletionSource<PluginStoreDialog>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var watchStore = Window.WindowOpenedEvent.AddClassHandler<PluginStoreDialog>((dialog, _) => storeOpened.TrySetResult(dialog));
            Task openStore = Task.CompletedTask;
            await HeadlessAvalonia.RunAsync(() =>
            {
                openStore = admin.InstallPluginCommand.ExecuteAsync(null);
                return Task.CompletedTask;
            });
            var storeDialog = await storeOpened.Task.WaitAsync(Until.Ceiling);
            PluginStoreDialogViewModel? storeViewModel = null;
            await HeadlessAvalonia.RunAsync(() =>
            {
                storeViewModel = storeDialog.DataContext as PluginStoreDialogViewModel;
                return Task.CompletedTask;
            });
            if (storeViewModel is null)
            {
                throw new InvalidOperationException("Server store did not open its view model.");
            }

            Assert.False(storeViewModel.CanManageStores);

            await HeadlessAvalonia.RunAsync(async () =>
            {
                await Until.CollectionHolds(storeViewModel.Manager.AvailablePlugins, () => storeViewModel.Manager.AvailablePlugins.Any(plugin => plugin.Id == StorePlugin));
            });
            var beforeChoice = await setup.GetAsync<JsonArray>("api/v1/plugins");
            Assert.DoesNotContain(beforeChoice, plugin => plugin?["id"]?.GetValue<string>() == StorePlugin);

            // AC-1525: the install walks into the consent for the bytes the server hashed; Allow pins them there.
            var consented = new TaskCompletionSource<PluginConsentInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var watchConsent = Window.WindowOpenedEvent.AddClassHandler<PluginConsentDialog>((consentDialog, _) =>
            {
                if (consentDialog.DataContext is PluginConsentInfo info)
                {
                    consented.TrySetResult(info);
                }
                else
                {
                    consented.TrySetException(new InvalidOperationException("The consent dialog opened without its plugin."));
                }

                Dispatcher.UIThread.Post(() => consentDialog.Close(true));
            });
            var installStatus = "";
            await HeadlessAvalonia.RunAsync(async () =>
            {
                var selected = storeViewModel.Manager.AvailablePlugins.Single(plugin => plugin.Id == StorePlugin);
                await storeViewModel.Manager.InstallFromStoreCommand.ExecuteAsync(selected);
                installStatus = storeViewModel.Manager.StatusMessage;
                storeDialog.Close();
            });
            await openStore;
            var consent = await consented.Task.WaitAsync(Until.Ceiling);
            Assert.True(admin.Plugins.Any(plugin => plugin.Id == StorePlugin), admin.Status);
            var installed = await setup.GetAsync<JsonArray>("api/v1/plugins");
            var serverEntry = installed.Single(plugin => plugin?["id"]?.GetValue<string>() == StorePlugin);
            Assert.Equal($"'Echo provider' enabled on {Server}. Restart the server to load it.", installStatus);
            Assert.Equal(serverEntry?["sha256"]?.GetValue<string>(), consent.Sha256);
            Assert.Equal((true, true), (serverEntry?["takesEffectAfterServerRestarts"]?.GetValue<bool>(), HeadlessAvalonia.Run(() => admin.NeedsRestart)));
            Assert.Equal(localPluginIds, view.Plugins.Plugins.Select(plugin => plugin.FolderId));

            // Restart now: the server stops as on a SIGTERM; the journey is its supervisor and starts it on the same state.
            await HeadlessAvalonia.RunAsync(() => admin.RestartServerCommand.ExecuteAsync(null));
            await run.Process.WaitForExitAsync().WaitAsync(Until.Ceiling);
            var restartExit = run.Process.ExitCode;
            run.Process.Dispose();
            run = startServer();
            await run.Running.WaitAsync(Until.Ceiling);
            var loaded = (await setup.GetAsync<JsonArray>("api/v1/plugins")).Single(plugin => plugin?["id"]?.GetValue<string>() == StorePlugin);
            var created = await setup.SendAsync<JsonObject>(HttpMethod.Post, "api/v1/profiles", new { label = "Echo from the store", provider = "echo-provider.echo" });
            var storePane = "";
            using (var storeSession = new CancellationTokenSource(Until.Ceiling))
            {
                var storeStarted = await setup.SendAsync<JsonObject>(HttpMethod.Post, "api/v1/sessions", new { profile = "Echo from the store", prompt = "from the store" });
                storePane = storeStarted["paneId"]?.GetValue<string>() ?? "";
                await setup.StreamEventsAsync(0, storeSession.Token)
                    .FirstAsync(evt => evt.Kind == "row" && evt.Data.GetRawText().Contains(storePane, StringComparison.Ordinal) && evt.Data.GetRawText().Contains("echo: from the store", StringComparison.Ordinal), storeSession.Token);
            }

            await HeadlessAvalonia.RunAsync(async () =>
            {
                var group = view.ServerGroups[0];
                await Until.Holds(group, () => group.IsConnected && group.IsAdminKey);
                await admin.LoadCommand.ExecuteAsync(null);
            });
            Assert.Equal((0, "load", false, "Echo from the store"), (restartExit, loaded?["decision"]?.GetValue<string>(), loaded?["takesEffectAfterServerRestarts"]?.GetValue<bool>(), created["label"]?.GetValue<string>()));

            // AC-1534: Admin → Profiles makes a profile on the fresh provider in that provider's own view, key included. No
            // answer, audit line or server log holds the key; a save that leaves it masked keeps it, one that moves the
            // base URL with it masked is refused, and a session on the profile reaches the provider with the key set.
            var providerKey = $"journey-provider-{Guid.NewGuid():N}";
            cockpit.Services.GetRequiredService<IPluginProviderConfigViews>().Register("echo-provider.echo", config => new _EchoConfigView(config));
            var (madeStatus, keptStatus, movedStatus) = ("", "", "");
            await HeadlessAvalonia.RunAsync(async () =>
            {
                var page = admin.Profiles ?? throw new InvalidOperationException("Admin has no Profiles page.");
                await page.LoadCommand.ExecuteAsync(null);
                page.StartNewCommand.Execute(null);
                page.EditorLabel = "Echo keyed";
                page.EditorProvider = "echo-provider.echo";
                var made = page.PluginConfigView as _EchoConfigView ?? throw new InvalidOperationException("The provider's own view did not open.");
                (made.BaseUrl.Text, made.ApiKey.Text) = ("https://inference.example/v1", providerKey);
                await page.SaveEditorCommand.ExecuteAsync(null);
                madeStatus = page.Status;

                page.StartEditCommand.Execute(page.Rows.Single(row => row.Label == "Echo keyed"));
                await page.SaveEditorCommand.ExecuteAsync(null);
                keptStatus = page.Status;

                page.StartEditCommand.Execute(page.Rows.Single(row => row.Label == "Echo keyed"));
                var moved = page.PluginConfigView as _EchoConfigView ?? throw new InvalidOperationException("The provider's own view did not open.");
                moved.BaseUrl.Text = "https://elsewhere.example/v1";
                await page.SaveEditorCommand.ExecuteAsync(null);
                movedStatus = page.Status;
                page.CancelEditorCommand.Execute(null);
            });
            var listedProfiles = (await setup.GetAsync<JsonObject>("api/v1/profiles")).ToJsonString();
            var (keyedAnswer, keyedPane) = ("", "");
            using (var keyedSession = new CancellationTokenSource(Until.Ceiling))
            {
                var keyedStarted = await setup.SendAsync<JsonObject>(HttpMethod.Post, "api/v1/sessions", new { profile = "Echo keyed", prompt = "config" });
                keyedPane = keyedStarted["paneId"]?.GetValue<string>() ?? "";
                var answered = await setup.StreamEventsAsync(0, keyedSession.Token)
                    .FirstAsync(evt => evt.Kind == "row" && evt.Data.GetRawText().Contains(keyedPane, StringComparison.Ordinal) && evt.Data.GetRawText().Contains("echo: base", StringComparison.Ordinal), keyedSession.Token);
                keyedAnswer = answered.Data.GetRawText();
            }

            Assert.Equal(("", "", "Enter the key again: this change sends it to a different address."), (madeStatus, keptStatus, movedStatus));
            Assert.Contains("echo: base https://inference.example/v1", keyedAnswer, StringComparison.Ordinal);
            Assert.Contains("key set", keyedAnswer, StringComparison.Ordinal);
            Assert.Contains("\"hasApiKey\":true", listedProfiles, StringComparison.Ordinal);
            Assert.False(listedProfiles.Contains(providerKey, StringComparison.Ordinal), "GET /profiles returned the provider key.");
            Assert.False((await setup.GetAsync<JsonArray>("api/v1/audit")).ToJsonString().Contains(providerKey, StringComparison.Ordinal), "The audit holds the provider key.");
            Assert.False(run.Output.Contains(providerKey, StringComparison.Ordinal), "The server log holds the provider key.");

            await HeadlessAvalonia.RunAsync(() => admin.RemovePluginCommand.ExecuteAsync(admin.Plugins.Single(plugin => plugin.Id == StorePlugin)));
            Assert.DoesNotContain(admin.Plugins, plugin => plugin.Id == StorePlugin);
            var removed = await setup.GetAsync<JsonArray>("api/v1/plugins");
            Assert.DoesNotContain(removed, plugin => plugin?["id"]?.GetValue<string>() == StorePlugin);

            // AC-1511: Admin → Plugins → Depot → Settings opens Depot's own view for the server. An installation token signs
            // the server's connection in at a fake Depot without a browser; the same token again is refused and adds nothing.
            var installToken = $"journey-install-{Guid.NewGuid():N}";
            await using var depot = await LoopbackHttpServer.StartAsync(_FakeDepot(installToken));
            var depotUrl = depot.BaseUrl.TrimEnd('/');
            var depotOpened = new TaskCompletionSource<Window>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var watchDepot = Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) =>
            {
                if (window.GetLogicalDescendants().OfType<TextBox>().Any(box => box.PlaceholderText == "Installation token"))
                {
                    depotOpened.TrySetResult(window);
                }
            });
            var depotHasSettings = false;
            await HeadlessAvalonia.RunAsync(() =>
            {
                var depotRow = admin.Plugins.Single(plugin => plugin.Id == "depot");
                depotHasSettings = depotRow.HasSettings;
                _ = admin.OpenPluginSettingsCommand.ExecuteAsync(depotRow);
                return Task.CompletedTask;
            });
            var depotWindow = await depotOpened.Task.WaitAsync(Until.Ceiling);
            var signedIn = "";
            var reused = "";
            await HeadlessAvalonia.RunAsync(async () =>
            {
                signedIn = await _ConnectDepotAsync(depotWindow, "Work", depotUrl, installToken);
                reused = await _ConnectDepotAsync(depotWindow, "Spare", depotUrl.Replace("127.0.0.1", "localhost", StringComparison.Ordinal), installToken);
                depotWindow.Close();
            });
            var serverState = "";
            await Until.ReloadHolds(
                async () => serverState = await File.ReadAllTextAsync(Path.Combine(stateRoot, "cockpit.json")),
                () => serverState.Contains("Depot: Work", StringComparison.Ordinal) && serverState.Contains(depotUrl, StringComparison.Ordinal) && !serverState.Contains("localhost", StringComparison.Ordinal));

            string? issued = null;
            await HeadlessAvalonia.RunAsync(async () =>
            {
                await Until.CollectionHolds(admin.Keys, () => admin.Keys.Any(key => key.Label == Laptop));
                admin.StartIssueCommand.Execute(null);
                admin.EditorLabel = Phone;
                await admin.SaveEditorCommand.ExecuteAsync(null);
                issued = admin.IssuedSecret;
                admin.DismissIssuedCommand.Execute(null);
            });
            var phoneKey = issued ?? throw new InvalidOperationException($"Issuing a key did not show it: {admin.Status}");

            // The new key connects: who it is, polled as a desktop does, and the event stream it holds open.
            using var phone = new BackendApiClient(baseAddress, phoneKey, fingerprint, TimeProvider.System);
            var who = await phone.WhoAmIAsync();
            await phone.WhoAmIAsync();
            using (var streaming = new CancellationTokenSource(Until.Ceiling))
            {
                var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var stream = Task.Run(async () =>
                {
                    try
                    {
                        await foreach (var _ in phone.StreamEventsAsync(null, streaming.Token, up => { if (up) { connected.TrySetResult(); } }))
                        {
                        }
                    }
                    catch (OperationCanceledException) when (streaming.IsCancellationRequested)
                    {
                    }
                });
                await connected.Task.WaitAsync(Until.Ceiling);
                await streaming.CancelAsync();
                await stream;
            }

            await HeadlessAvalonia.RunAsync(() => admin.RevokeCommand.ExecuteAsync(admin.Keys.Single(key => key.Label == Phone)));
            var refused = await Assert.ThrowsAsync<BackendApiException>(() => phone.WhoAmIAsync());

            // Ten unknown keys from this address lock it out; the keys that work pass a lockout, so the admin can lift it.
            for (var attempt = 0; attempt < ConnectKeyPolicy.Default.FailuresBeforeLockout; attempt++)
            {
                using var stranger = new BackendApiClient(baseAddress, _NewKey(), fingerprint, TimeProvider.System);
                await Assert.ThrowsAsync<BackendApiException>(() => stranger.WhoAmIAsync());
            }

            var lockedOut = "";
            await HeadlessAvalonia.RunAsync(async () =>
            {
                await admin.LoadCommand.ExecuteAsync(null);
                var lockout = admin.Lockouts.Single();
                lockedOut = lockout.Address;
                await admin.LiftCommand.ExecuteAsync(lockout);
                await admin.LoadCommand.ExecuteAsync(null);
            });

            // Projects: the server clones over file:// and adds the project; "+ Start on" offers it, and a session starts there.
            var cloneResult = "";
            var offered = new List<string>();
            var paneId = "";
            await HeadlessAvalonia.RunAsync(async () =>
            {
                admin.CloneUrl = new Uri(origin).AbsoluteUri;
                admin.CloneBranch = "main";
                admin.CloneName = Project;
                await admin.CloneCommand.ExecuteAsync(null);
                cloneResult = admin.CloneResult;

                var group = view.ServerGroups[0];
                await view.OpenServerStartCommand.ExecuteAsync(group);
                offered = [.. group.Start.Projects.Select(project => project.Name)];
                group.Start.SelectedProfile = group.Start.Profiles.First(profile => profile.Label == "Echo");
                group.Start.SelectedProject = group.Start.Projects.First(project => project.Name == Project);
                await view.StartOnServerCommand.ExecuteAsync(group);
                paneId = group.Sessions.Single(session => session.Handle.PaneId != storePane && session.Handle.PaneId != keyedPane).Handle.PaneId;
            });

            // AC-1514: From Depot offers the fixture source's project; "Add on" clones the repository its definition names
            // and stores it bound, it leaves the list, a second add is refused, and "+ Start on" starts a session in it.
            var sharedOffered = new List<string>();
            var cloneable = ((await setup.GetAsync<JsonObject>("api/v1/projects/shared"))["sources"]?.AsArray() ?? [])
                .SelectMany(source => source?["projects"]?.AsArray() ?? [])
                .ToDictionary(project => project?["id"]?.GetValue<string>() ?? "", project => project?["cloneable"]?.GetValue<bool>() ?? false);
            ServerSharedProjectRowViewModel? bareRow = null;
            var sharedResult = "";
            var sharedAfter = new List<string>();
            var sharedPane = "";
            await HeadlessAvalonia.RunAsync(async () =>
            {
                sharedOffered = [.. admin.SharedProjects.Select(project => project.Id)];
                bareRow = admin.SharedProjects.Single(project => project.Id == BareProject);
                admin.SharedProfile = "Echo";
                await admin.AddSharedCommand.ExecuteAsync(admin.SharedProjects.Single(project => project.Id == SharedProject));
                sharedResult = admin.CloneResult;
                sharedAfter = [.. admin.SharedProjects.Select(project => project.Id)];

                var group = view.ServerGroups[0];
                var running = group.Sessions.Select(session => session.Handle.PaneId).ToHashSet();
                await view.OpenServerStartCommand.ExecuteAsync(group);
                group.Start.SelectedProfile = group.Start.Profiles.First(profile => profile.Label == "Echo");
                group.Start.SelectedProject = group.Start.Projects.First(project => project.Name == SharedName);
                await view.StartOnServerCommand.ExecuteAsync(group);
                sharedPane = group.Sessions.Single(session => !running.Contains(session.Handle.PaneId)).Handle.PaneId;
            });
            var bareAdd = await Assert.ThrowsAsync<BackendApiException>(() =>
                setup.SendAsync<JsonObject>(HttpMethod.Post, "api/v1/projects/shared", new { sharedProjectId = BareProject, profile = "Echo" }));
            var secondAdd = await Assert.ThrowsAsync<BackendApiException>(() =>
                setup.SendAsync<JsonObject>(HttpMethod.Post, "api/v1/projects/shared", new { sharedProjectId = SharedProject, profile = "Echo" }));
            var bound = (await new ProjectStore(CockpitConfigPath.For(stateRoot)).LoadAsync()).Projects.Single(project => project.Name == SharedName);
            var sharedFolder = (await File.ReadAllLinesAsync(Path.Combine(stateRoot, "session-state.jsonl")))
                .Select(line => JsonNode.Parse(line))
                .Last(record => record?["PaneId"]?.GetValue<string>() == sharedPane)?["WorkingDirectory"]?.GetValue<string>();
            var listed = (await setup.GetAsync<JsonObject>("api/v1/projects"))["projects"]?.AsArray().Single(project => project?["name"]?.GetValue<string>() == Project);
            var clonePath = listed?["path"]?.GetValue<string>() ?? "";
            var sessionFolder = (await File.ReadAllLinesAsync(Path.Combine(stateRoot, "session-state.jsonl")))
                .Select(line => JsonNode.Parse(line))
                .Last(record => record?["PaneId"]?.GetValue<string>() == paneId)?["WorkingDirectory"]?.GetValue<string>();
            List<(string Key, string What)> audit = [];
            await Until.ReloadHolds(
                () => HeadlessAvalonia.RunAsync(async () =>
                {
                    await admin.LoadCommand.ExecuteAsync(null);
                    audit = admin.Audit.Select(row => (row.Key, row.What)).ToList();
                }),
                () => audit.Contains((Laptop, $"issued · {Phone} (operate)"))
                    && audit.Contains((Phone, "connected"))
                    && audit.Contains((Laptop, $"revoked · {Phone}"))
                    && audit.Contains((Phone, "refused: revoked key"))
                    && audit.Contains((Laptop, $"lockout lifted · {lockedOut}")));
            var lockoutsAfter = HeadlessAvalonia.Run(() => admin.Lockouts.Count);

            // AC-1473: Profiles → Echo gets another model; a new session on Echo starts on the server with that model.
            var profiles = admin.Profiles ?? throw new InvalidOperationException("Admin opened Options without its Profiles page.");
            await HeadlessAvalonia.RunAsync(async () =>
            {
                await Until.CollectionHolds(profiles.Rows, () => profiles.Rows.Any(row => row.Label == "Echo"));
                profiles.StartEditCommand.Execute(profiles.Rows.Single(row => row.Label == "Echo"));
                profiles.Editor.Model = "echo-large";
                await profiles.SaveEditorCommand.ExecuteAsync(null);
            });
            using var timeout = new CancellationTokenSource(Until.Ceiling);
            var started = await setup.SendAsync<JsonObject>(HttpMethod.Post, "api/v1/sessions", new { profile = "Echo", prompt = "hello" });
            var echoPane = started["paneId"]?.GetValue<string>() ?? "";
            var echoed = await setup.StreamEventsAsync(0, timeout.Token)
                .FirstAsync(evt => evt.Kind == "row" && evt.Data.GetRawText().Contains(echoPane, StringComparison.Ordinal) && evt.Data.GetRawText().Contains("echo: hello", StringComparison.Ordinal), timeout.Token);
            var profilesStatus = HeadlessAvalonia.Run(() => profiles.Status);

            // AC-1475: Assistant → Copy in Echo → turn it on; the server reports it running, and both are in the Audit log.
            var assistant = admin.Assistant ?? throw new InvalidOperationException("Admin opened Options without its Assistant page.");
            await HeadlessAvalonia.RunAsync(async () =>
            {
                dialog.SelectCategory("server-assistant");
                await Until.LayoutHolds(dialog, () => dialog.GetVisualDescendants().OfType<ServerAssistantPage>().Count() == 1);
                await assistant.LoadCommand.ExecuteAsync(null);
                assistant.SelectedCopyChoice = assistant.CopyChoices.Single(choice => choice.Label == "Echo");
                await assistant.CopyInCommand.ExecuteAsync(null);
                assistant.IsEnabled = true;
                await Until.Holds(assistant, () => assistant.IsRunning);
            });
            List<(string Key, string What)> assistantAudit = [];
            await Until.ReloadHolds(
                () => HeadlessAvalonia.RunAsync(async () =>
                {
                    await admin.LoadCommand.ExecuteAsync(null);
                    assistantAudit = admin.Audit.Select(row => (row.Key, row.What)).ToList();
                }),
                () => assistantAudit.Contains((Laptop, "assistant profile copied from Echo")) && assistantAudit.Contains((Laptop, "assistant turned on")));
            var assistantState = HeadlessAvalonia.Run(() => (assistant.AvailabilityTitle, assistant.MessageTitle));
            await HeadlessAvalonia.RunAsync(async () =>
            {
                dialog.Close();
                await adminClosed;
            });

            Assert.Equal((Phone, "operate"), (who.Label, who.Capability));
            Assert.Equal(HttpStatusCode.Unauthorized, refused.Status);
            Assert.Equal(0, lockoutsAfter);
            Assert.Equal("", admin.Status);
            Assert.Contains((Laptop, $"issued · {Phone} (operate)"), audit);
            Assert.Single(audit, row => row == (Phone, "connected"));
            Assert.Contains((Laptop, $"revoked · {Phone}"), audit);
            Assert.Contains((Phone, "refused: revoked key"), audit);
            Assert.Contains((Laptop, $"lockout lifted · {lockedOut}"), audit);
            Assert.StartsWith($"Cloned into {clonePath} · ", cloneResult, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(clonePath, "README.md")), "The clone the server reported holds no checkout.");
            Assert.Contains(Project, offered);
            Assert.Equal(clonePath, sessionFolder);
            Assert.Contains((Laptop, $"cloned · {Project}"), audit);
            Assert.Contains(SharedProject, sharedOffered);
            // DEP-381: a definition without a repository is offered as not cloneable, its "Add on" is disabled with the reason,
            // and the server still refuses it.
            Assert.Equal((true, false), (cloneable[SharedProject], cloneable[BareProject]));
            Assert.Contains("names no repository", bareRow?.NoRepositoryHint, StringComparison.Ordinal);
            Assert.False(bareRow?.HasRepository);
            Assert.Equal(HttpStatusCode.BadRequest, bareAdd.Status);
            Assert.StartsWith($"Cloned into {bound.SourceDirectory} · ", sharedResult, StringComparison.Ordinal);
            Assert.DoesNotContain(SharedProject, sharedAfter);
            Assert.Equal(HttpStatusCode.Conflict, secondAdd.Status);
            Assert.Equal(new ProjectResource(SharedProject, ProjectResourceRole.Memory), bound.Resources[0]);
            Assert.Equal(new[] { "cockpit-sessions" }, bound.McpOverlay.EnabledServerNames);
            Assert.Equal(("Answer in echoes.", "Echo"), (bound.BehaviorPrompt, bound.DefaultProfileLabel));
            Assert.True(File.Exists(Path.Combine(bound.SourceDirectory ?? "", "README.md")), "The shared project's clone holds no checkout.");
            Assert.Equal(bound.SourceDirectory, sharedFolder);
            Assert.Equal("", profilesStatus);
            Assert.Contains("model echo-large", echoed.Data.GetRawText(), StringComparison.Ordinal);
            Assert.Equal(("Running", ""), assistantState);
            Assert.Contains((Laptop, "assistant profile copied from Echo"), assistantAudit);
            Assert.Contains((Laptop, "assistant turned on"), assistantAudit);
            Assert.True(depotHasSettings, "Admin → Plugins offered no Settings for Depot.");
            Assert.Equal(("Signed in.", "That installation token is not valid any more. Make a new one in Depot under Account."), (signedIn, reused));
            Assert.Contains((Laptop, "plugin_channel"), assistantAudit);
            var auditTrail = await File.ReadAllTextAsync(Path.Combine(stateRoot, "node-access-audit.jsonl"));
            Assert.False(
                new[] { serverState, run.Output, auditTrail }.Any(text => text.Contains(installToken, StringComparison.Ordinal)),
                "The installation token is in the server's state, its output or its audit.");
        }
        finally
        {
            if (!run.Process.HasExited)
            {
                run.Process.Kill(entireProcessTree: true);
            }

            run.Process.Dispose();
            JourneyHost.RemoveStateRoot(root);
        }
    }

    // Depot as the server reaches it (AC-1511, DEP-264 not yet rolled out): its OAuth metadata and a token endpoint that
    // redeems one installation token once, for Depot's own client.
    private static RequestDelegate _FakeDepot(string installToken)
    {
        var redeemed = 0;
        return async context =>
        {
            var origin = $"{context.Request.Scheme}://{context.Request.Host}";
            switch (context.Request.Path.Value)
            {
                case "/.well-known/oauth-protected-resource":
                    await context.Response.WriteAsJsonAsync(new { resource = $"{origin}/mcp", authorization_servers = new[] { origin } });
                    return;
                case "/.well-known/oauth-authorization-server":
                    await context.Response.WriteAsJsonAsync(new { issuer = origin, token_endpoint = $"{origin}/connect/token" });
                    return;
                case "/connect/token":
                    var form = await context.Request.ReadFormAsync();
                    if (form["grant_type"] == "urn:depot:grant-type:install_token" && form["client_id"] == "depot"
                        && form["install_token"] == installToken && Interlocked.Exchange(ref redeemed, 1) == 0)
                    {
                        await context.Response.WriteAsJsonAsync(new { access_token = "journey-access", token_type = "Bearer", expires_in = 3600, refresh_token = "journey-refresh", scope = "depot" });
                        return;
                    }

                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsJsonAsync(new { error = "invalid_grant" });
                    return;
                default:
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
            }
        };
    }

    // Fills Depot's server view and presses Connect, returning the sentence it answers with.
    private static async Task<string> _ConnectDepotAsync(Window window, string name, string url, string installToken)
    {
        var view = window.GetLogicalDescendants().OfType<UserControl>().First(control => control.GetType().Name == "DepotServerSettingsControl");
        var boxes = view.GetLogicalDescendants().OfType<TextBox>().ToList();
        (boxes[0].Text, boxes[1].Text, boxes[2].Text) = (name, url, installToken);
        var status = view.GetLogicalDescendants().OfType<TextBlock>().Last();
        var before = status.Text;
        view.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, "Connect")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until.Holds(status, () => status.Text is { Length: > 0 } text && text != before && text != "Connecting…");
        return status.Text ?? "";
    }

    private static async Task _GitAsync(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git", arguments) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        using var git = Process.Start(start) ?? throw new InvalidOperationException("git did not start.");
        var error = git.StandardError.ReadToEndAsync();
        await git.StandardOutput.ReadToEndAsync();
        await git.WaitForExitAsync();
        Assert.True(git.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {await error}");
    }

    // A key no one issued, in the shape of one: "ck_" and 64 random characters.
    private static string _NewKey() =>
        "ck_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    // AC-1525: the echo provider itself, so the profile and session after the restart run on what the store installed.
    private static PluginStoreConfig _CreateFixtureStore(string root)
    {
        var store = Directory.CreateDirectory(Path.Combine(root, "fixture-store")).FullName;
        var zip = Path.Combine(store, $"{StorePlugin}-1.0.0.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var file in Directory.EnumerateFiles(ServerJourney._Metadata("EchoProviderDirectory")))
            {
                var entry = archive.CreateEntry(Path.GetFileName(file));
                using var source = File.OpenRead(file);
                using var destination = entry.Open();
                source.CopyTo(destination);
            }
        }

        var hash = PluginHash.Compute(File.ReadAllBytes(zip));
        File.WriteAllText(Path.Combine(store, "index.json"), $$"""{"name":"Journey store","plugins":[{"id":"{{StorePlugin}}","name":"Echo provider","description":null,"author":null,"latestVersion":"1.0.0","versions":[{"version":"1.0.0","path":"{{StorePlugin}}-1.0.0.zip","abstractionsVersion":3,"minHostVersion":null,"sha256":"{{hash}}","notes":null}]}]}""");
        return PluginStoreConfig.Local(store);
    }

    // Above the offset, so the pairing port below it is a valid one.
    private static int _FreeDoorPort()
    {
        while (true)
        {
            using var listener = new System.Net.Sockets.TcpListener(IPAddress.Any, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            if (port > NodeEndpointSettings.McpPortOffset)
            {
                return port;
            }
        }
    }

    // AC-1534: a provider's own profile view, as a plugin's UI part registers one: base URL and key. It opens every echo
    // profile, the keyless ones of the steps after it too.
    private sealed class _EchoConfigView : IPluginProviderConfigView
    {
        public _EchoConfigView(string? configJson)
        {
            var config = configJson is null ? null : JsonNode.Parse(configJson);
            BaseUrl.Text = config?["baseUrl"]?.GetValue<string>();
            ApiKey.Text = config?["apiKey"]?.GetValue<string>();
            View = new StackPanel { Children = { BaseUrl, ApiKey } };
        }

        public TextBox BaseUrl { get; } = new();

        public TextBox ApiKey { get; } = new() { PasswordChar = '*' };

        public Control View { get; }

        public bool TryGetConfigJson(out string configJson)
        {
            configJson = new JsonObject { ["baseUrl"] = BaseUrl.Text ?? "", ["apiKey"] = ApiKey.Text ?? "" }.ToJsonString();
            return true;
        }
    }
}

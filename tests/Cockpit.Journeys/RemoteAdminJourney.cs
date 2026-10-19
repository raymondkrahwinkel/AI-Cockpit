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
using Cockpit.Core.Configuration;
using Cockpit.Core.Mcp;
using Cockpit.Core.Plugins;
using Cockpit.Core.Projects;
using Cockpit.Infrastructure.BackendApi;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Infrastructure.Projects;
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
        var checksumFile = Path.Combine(root, "echo-checksum.txt");
        var port = _FreeDoorPort();
        var (fingerprint, _, _) = await ServerJourney._PrepareStateRootAsync(stateRoot, port - NodeEndpointSettings.McpPortOffset, root, "http://127.0.0.1:9/webhook", pluginStore: store, withEchoPlugin: false);
        var keyFile = ServerJourney._Secret(root, "connect-key", bootstrap);
        ServerJourney.ServerRun startServer() => ServerJourney._RunServer(
            ServerJourney._Metadata("CockpitServerDirectory"), stateRoot, keyFile,
            new Dictionary<string, string> { ["COCKPIT_ALLOW_FILE_CLONES"] = "1", ["COCKPIT_ECHO_SHARED_REPOSITORY"] = new Uri(shared).AbsoluteUri, ["COCKPIT_ECHO_SHARED_CHECKSUM_FILE"] = checksumFile });
        var run = startServer();
        try
        {
            await run.Running.WaitAsync(Until.Ceiling);
            var baseAddress = new Uri($"https://127.0.0.1:{port}/");
            using var setup = new BackendApiClient(baseAddress, bootstrap, fingerprint, TimeProvider.System);
            var laptop = await setup.SendAsync<JsonObject>(HttpMethod.Post, "api/v1/keys", new { label = Laptop, capability = "admin" });
            var laptopKey = laptop["secret"]?.GetValue<string>() ?? throw new InvalidOperationException("The server issued no key.");

            // AC-1571: the echo fixture on this desktop too, whose own settings view Admin opens for the server's copy.
            var desktopState = Path.Combine(root, "desktop-state");
            await _InstallLocallyAsync(desktopState, Path.Combine(root, "desktop-bundled"), StorePlugin, "EchoProviderDirectory");
            await using var cockpit = JourneyHost.Desktop(stateRoot: desktopState);
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

            // AC-1534/AC-1585: Admin → Profiles is the editor Options › Profiles uses, with the server behind it. A profile on
            // the fresh provider gets every field that editor has, in the provider's own view, key and a secret variable
            // included. Reopened from the server it shows each plain field as saved and each secret only as set; a save
            // that leaves the secrets masked keeps them, one that moves the base URL with the key masked is refused.
            var providerKey = $"journey-provider-{Guid.NewGuid():N}";
            var variableSecret = $"journey-variable-{Guid.NewGuid():N}";
            var workFolder = Directory.CreateDirectory(Path.Combine(root, "profile-work")).FullName;
            var (madeStatus, keptStatus, movedStatus, editorKind) = ("", "", "", "");
            string[] reopenedProfile = [];
            await HeadlessAvalonia.RunAsync(async () =>
            {
                var page = admin.Profiles ?? throw new InvalidOperationException("Admin has no Profiles page.");
                dialog.SelectCategory("server-profiles");
                await page.LoadCommand.ExecuteAsync(null);
                await Until.LayoutHolds(dialog, () => dialog.GetVisualDescendants().OfType<ProfilesEditor>().Count() == 1);
                editorKind = dialog.GetVisualDescendants().OfType<ProfilesEditor>().Single().DataContext?.GetType().Name ?? "";
                var editor = page.Editor ?? throw new InvalidOperationException("The server's profiles did not load.");
                editor.AddProfileCommand.Execute(null);
                var made = editor.SelectedProfile ?? throw new InvalidOperationException("Add profile selected nothing.");
                made.SelectedProvider = made.Providers.Single(provider => provider.PluginProviderId == "echo-provider.echo");
                made.Label = "Echo keyed";
                (_EchoField(made, "BaseUrl").Text, _EchoField(made, "ApiKey").Text) = ("https://inference.example/v1", providerKey);
                made.Purpose = "journey purpose";
                made.DefaultWorkingDirectory = workFolder;
                made.Assistant = "Journey";
                made.ProfileSystemPrompt = "journey standing instructions";
                made.MemoryCapMegabytes = 4096;
                made.AutoApproveTools = true;
                made.SelectedReadingLevel = made.ReadingLevels.Last();
                made.SelectDefaultKindSdkCommand.Execute(null);
                made.PluginOptionDefaults.Single(option => option.Key == "model").Value = "echo-large";
                made.PluginOptionDefaults.Single(option => option.Key == "effort").Value = "high";
                made.AllowedAsTarget = true;
                made.DelegationPurpose = "journey delegation";
                made.AllowedTaskTypes = "summarize, refactor";
                made.AllowedWorkingDirs = workFolder;
                made.PermissionCeiling = "acceptEdits";
                made.AllowedTools = "journey_tool";
                made.MaxConcurrentTasks = 3;
                made.DelegationTimeoutMinutes = 7;
                made.MayDelegateFurther = true;
                made.RestrictMcpServers = true;
                made.EnvironmentVariables.Add(new ProfileEnvironmentVariableViewModel("JOURNEY_REGION", "eu-west", false));
                made.EnvironmentVariables.Add(new ProfileEnvironmentVariableViewModel("JOURNEY_VARIABLE", variableSecret, true));
                await page.SaveCommand.ExecuteAsync(null);
                madeStatus = page.Status;

                var again = page.Editor?.Profiles.Single(profile => profile.Label == "Echo keyed") ?? throw new InvalidOperationException("The saved profile did not come back.");
                reopenedProfile =
                [
                    again.Label, again.Purpose, again.DefaultWorkingDirectory, again.Assistant, again.ProfileSystemPrompt, $"{again.MemoryCapMegabytes}",
                    $"{again.AutoApproveTools}", again.SelectedReadingLevel.Label, $"{again.SelectedDefaultKind}",
                    string.Join(",", again.PluginOptionDefaults.Select(option => $"{option.Key}={option.Value}")),
                    $"{again.AllowedAsTarget}", again.DelegationPurpose, again.AllowedTaskTypes, again.AllowedWorkingDirs, again.PermissionCeiling,
                    again.AllowedTools, $"{again.MaxConcurrentTasks}", $"{again.DelegationTimeoutMinutes}", $"{again.MayDelegateFurther}", $"{again.RestrictMcpServers}",
                    _EchoField(again, "BaseUrl").Text ?? "", _EchoField(again, "ApiKey").Text ?? "",
                    string.Join(",", again.EnvironmentVariables.Select(variable => $"{variable.Key}={variable.Value}:{variable.IsSecret}")),
                ];

                await page.SaveCommand.ExecuteAsync(null);
                keptStatus = page.Status;

                var moved = page.Editor?.Profiles.Single(profile => profile.Label == "Echo keyed") ?? throw new InvalidOperationException("The saved profile did not come back.");
                _EchoField(moved, "BaseUrl").Text = "https://elsewhere.example/v1";
                await page.SaveCommand.ExecuteAsync(null);
                movedStatus = page.Status;
                await page.LoadCommand.ExecuteAsync(null);
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

            Assert.Equal(("ManageProfilesDialogViewModel", "Saved on the server.", "Saved on the server.", "Enter the key again: this change sends it to a different address."), (editorKind, madeStatus, keptStatus, movedStatus));
            Assert.Equal(
            [
                "Echo keyed", "journey purpose", workFolder, "Journey", "journey standing instructions", "4096", "True", SessionOptionCatalog.ReadingLevels[^1].Label, "Sdk",
                "model=echo-large,effort=high", "True", "journey delegation", "summarize, refactor", workFolder, "acceptEdits", "journey_tool", "3", "7", "True", "True",
                "https://inference.example/v1", "(set on the server)", "JOURNEY_REGION=eu-west:False,JOURNEY_VARIABLE=(set on the server):True",
            ], reopenedProfile);
            Assert.Contains("echo: base https://inference.example/v1", keyedAnswer, StringComparison.Ordinal);
            Assert.Contains("key set", keyedAnswer, StringComparison.Ordinal);
            Assert.Contains("\"hasApiKey\":true", listedProfiles, StringComparison.Ordinal);
            Assert.False(listedProfiles.Contains(providerKey, StringComparison.Ordinal), "GET /profiles returned the provider key.");
            Assert.False(listedProfiles.Contains(variableSecret, StringComparison.Ordinal), "GET /profiles returned the secret variable.");
            Assert.False((await setup.GetAsync<JsonArray>("api/v1/audit")).ToJsonString().Contains(variableSecret, StringComparison.Ordinal), "The audit holds the secret variable.");
            Assert.False(run.Output.Contains(variableSecret, StringComparison.Ordinal), "The server log holds the secret variable.");
            Assert.False((await setup.GetAsync<JsonArray>("api/v1/audit")).ToJsonString().Contains(providerKey, StringComparison.Ordinal), "The audit holds the provider key.");
            Assert.False(run.Output.Contains(providerKey, StringComparison.Ordinal), "The server log holds the provider key.");

            // AC-1571: a plugin's own settings view, unchanged, over the server's storage. Git status (nobody adapted it) saves
            // there, not here. The echo fixture's credential reads as set on the server, reopens as only that, survives an
            // untouched Save and clears when emptied; no answer, audit line or log holds it.
            await _SaveServerSettingsAsync(admin, "git-status", window => window.GetLogicalDescendants().OfType<CheckBox>().Any(), window =>
            {
                window.GetLogicalDescendants().OfType<CheckBox>().Single().IsChecked = false;
                return Task.CompletedTask;
            });
            var gitStatusOnServer = await setup.GetAsync<JsonObject>("api/v1/plugins/git-status/storage");
            var gitStatusHere = cockpit.Services.GetRequiredService<PluginUiManager>().UiPartOf("git-status")?.Host.Storage.Get<bool?>("showBranchName");

            var echoKey = $"journey-plugin-key-{Guid.NewGuid():N}";
            await _SaveServerSettingsAsync(admin, StorePlugin, _HoldsApiToken, window =>
            {
                _Box(window, "Greeting").Text = "hello from the desktop";
                _Box(window, "API token").Text = echoKey;
                return Task.CompletedTask;
            });
            var afterSave = await _ReadEchoSettingsAsync(setup);
            var reopened = "";
            await _SaveServerSettingsAsync(admin, StorePlugin, _HoldsApiToken, window =>
            {
                reopened = _Box(window, "API token").Text ?? "";
                return Task.CompletedTask;
            });
            var afterKeep = await _ReadEchoSettingsAsync(setup);
            var echoOnServer = (await setup.GetAsync<JsonObject>($"api/v1/plugins/{StorePlugin}/storage")).ToJsonString();
            await _SaveServerSettingsAsync(admin, StorePlugin, _HoldsApiToken, window =>
            {
                _Box(window, "API token").Text = "";
                return Task.CompletedTask;
            });
            var afterClear = await _ReadEchoSettingsAsync(setup);

            Assert.Equal((false, (bool?)null), (gitStatusOnServer["showBranchName"]?.GetValue<bool>(), gitStatusHere));
            Assert.Equal(
                (("hello from the desktop", true), "(set on the server)", ("hello from the desktop", true), ("hello from the desktop", false)),
                (afterSave, reopened, afterKeep, afterClear));
            Assert.False(echoOnServer.Contains(echoKey, StringComparison.Ordinal), "GET /storage returned the plugin's key.");
            Assert.False((await setup.GetAsync<JsonArray>("api/v1/audit")).ToJsonString().Contains(echoKey, StringComparison.Ordinal), "The audit holds the plugin's key.");
            Assert.False(run.Output.Contains(echoKey, StringComparison.Ordinal), "The server log holds the plugin's key.");

            await HeadlessAvalonia.RunAsync(() => admin.RemovePluginCommand.ExecuteAsync(admin.Plugins.Single(plugin => plugin.Id == StorePlugin)));
            Assert.DoesNotContain(admin.Plugins, plugin => plugin.Id == StorePlugin);
            var removed = await setup.GetAsync<JsonArray>("api/v1/plugins");
            Assert.DoesNotContain(removed, plugin => plugin?["id"]?.GetValue<string>() == StorePlugin);

            // AC-1572 through AC-1571: Depot's own local view over the server. A browser sign-in is not on offer there (the
            // row reads back what the server stored first); a token signs Work in at a fake Depot, the same token on Spare is
            // refused, Remove takes Spare off the server, and the view reopened lists Work alone, signed in.
            var installToken = $"journey-install-{Guid.NewGuid():N}";
            await using var depot = await LoopbackHttpServer.StartAsync(_FakeDepot(installToken));
            var depotUrl = depot.BaseUrl.TrimEnd('/');
            var depotHasSettings = HeadlessAvalonia.Run(() => admin.Plugins.Single(plugin => plugin.Id == "depot").HasSettings);
            var (browser, signedIn, reused, reopenedDepot) = ("", "", "", "");
            await _SaveServerSettingsAsync(admin, "depot", _IsDepotView, async window =>
            {
                var work = _DepotRows(window)[0];
                _FillDepotRow(work, "Work", depotUrl, "");
                browser = await _PressDepotAsync(work, "Sign in");
                _Box(work, DepotTokenBox).Text = installToken;
                signedIn = await _PressDepotAsync(work, "Sign in with token");
                _Click(window, "+ Add connection");
                var spare = _DepotRows(window)[1];
                _FillDepotRow(spare, "Spare", depotUrl.Replace("127.0.0.1", "localhost", StringComparison.Ordinal), installToken);
                reused = await _PressDepotAsync(spare, "Sign in with token");
                _Click(spare, "Remove connection");
            });
            await _SaveServerSettingsAsync(admin, "depot", _IsDepotView, async window =>
            {
                var rows = _DepotRows(window);
                var status = _DepotStatus(rows[0]);
                await Until.Holds(status, () => status.Text == "Signed in.");
                reopenedDepot = $"{rows.Count} {_Box(rows[0], "Name (e.g. Work, Personal)").Text}: {status.Text}";
            });
            var serverState = "";
            await Until.ReloadHolds(
                async () => serverState = await _ReadSharedAsync(Path.Combine(stateRoot, "cockpit.json")),
                () => serverState.Contains("Depot: Work", StringComparison.Ordinal) && serverState.Contains(depotUrl, StringComparison.Ordinal)
                    && !serverState.Contains("localhost", StringComparison.Ordinal) && !serverState.Contains("Depot: Spare", StringComparison.Ordinal));

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
            // AC-1570: the first "Sync now" lays the baseline, the definition's checksum moves, the second reports it, and
            // the list says so; a third, with nothing new, clears the flag again.
            var syncRoute = $"api/v1/projects/{Uri.EscapeDataString(bound.Id)}/sync";
            JsonNode? _FlagOf(JsonObject list) => list["projects"]?.AsArray().Single(project => project?["id"]?.GetValue<string>() == bound.Id)?["remoteChanged"];
            await setup.SendAsync<JsonObject>(HttpMethod.Post, syncRoute, null);
            var flagBefore = _FlagOf(await setup.GetAsync<JsonObject>("api/v1/projects"))?.GetValue<bool>();
            await File.WriteAllTextAsync(checksumFile, "echo-2");
            await setup.SendAsync<JsonObject>(HttpMethod.Post, syncRoute, null);
            var flagAfter = _FlagOf(await setup.GetAsync<JsonObject>("api/v1/projects"))?.GetValue<bool>();
            await setup.SendAsync<JsonObject>(HttpMethod.Post, syncRoute, null);
            var flagCleared = _FlagOf(await setup.GetAsync<JsonObject>("api/v1/projects"))?.GetValue<bool>();
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
                await profiles.LoadCommand.ExecuteAsync(null);
                var echo = profiles.Editor?.Profiles.Single(profile => profile.Label == "Echo") ?? throw new InvalidOperationException("The server's profiles did not load.");
                echo.PluginOptionDefaults.Single(option => option.Key == "model").Value = "echo-large";
                await profiles.SaveCommand.ExecuteAsync(null);
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
            Assert.Equal((false, true, false), (flagBefore, flagAfter, flagCleared));
            Assert.Equal("Saved on the server.", profilesStatus);
            Assert.Contains("model echo-large", echoed.Data.GetRawText(), StringComparison.Ordinal);
            Assert.Equal(("Running", ""), assistantState);
            Assert.Contains((Laptop, "assistant profile copied from Echo"), assistantAudit);
            Assert.Contains((Laptop, "assistant turned on"), assistantAudit);
            Assert.True(depotHasSettings, "Admin → Plugins offered no Settings for Depot.");
            Assert.Equal(
                ("Sign-in isn't available for this connection right now.", "Signed in.", "That installation token is not valid any more. Make a new one in Depot under Account.", "1 Work: Signed in."),
                (browser, signedIn, reused, reopenedDepot));
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

    // AC-1571: opens Admin → Plugins → Settings for `pluginId`, lets `edit` change the view and saves it; the window
    // closes only once the server took the save, so a refusal fails here with what the window said.
    private static async Task _SaveServerSettingsAsync(ServerAdminViewModel admin, string pluginId, Func<Window, bool> isTheView, Func<Window, Task> edit)
    {
        var opened = new TaskCompletionSource<Window>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watch = Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) =>
        {
            if (isTheView(window))
            {
                opened.TrySetResult(window);
            }
        });
        await HeadlessAvalonia.RunAsync(() =>
        {
            _ = admin.OpenPluginSettingsCommand.ExecuteAsync(admin.Plugins.Single(plugin => plugin.Id == pluginId));
            return Task.CompletedTask;
        });
        await Task.WhenAny(opened.Task, Task.Delay(Until.Ceiling));
        var rows = HeadlessAvalonia.Run(() => string.Join(", ", admin.Plugins.Select(plugin => $"{plugin.Id}:{plugin.HasSettings}")));
        Assert.True(opened.Task.IsCompleted, $"No settings window opened for {pluginId}: '{HeadlessAvalonia.Run(() => admin.Status)}' ({rows})");
        var settings = await opened.Task;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // A view that throws in a click handler would stop the UI thread; caught here, it fails this step with its reason.
        Exception? thrown = null;
        void caught(object? sender, DispatcherUnhandledExceptionEventArgs e)
        {
            thrown ??= e.Exception;
            e.Handled = true;
        }

        HeadlessAvalonia.Run(() => Dispatcher.UIThread.UnhandledException += caught);
        try
        {
            var edited = HeadlessAvalonia.RunAsync(async () =>
            {
                settings.Closed += (_, _) => closed.TrySetResult();
                await edit(settings);
                _Click(settings, "Save");
            });
            await Task.WhenAny(edited, Task.Delay(Until.Ceiling * 2));
            Assert.True(thrown is null, $"{pluginId}'s settings view threw: {thrown?.Message}");
            await edited.WaitAsync(TimeSpan.Zero);
        }
        finally
        {
            HeadlessAvalonia.Run(() => Dispatcher.UIThread.UnhandledException -= caught);
        }
        await Task.WhenAny(closed.Task, Task.Delay(Until.Ceiling));
        var said = HeadlessAvalonia.Run(() => string.Join(" | ", settings.GetLogicalDescendants().OfType<TextBlock>().Where(text => text.IsVisible).Select(text => text.Text)));
        Assert.True(closed.Task.IsCompleted, $"The settings window stayed open: {said}");
    }

    // Into the desktop's own state root before its host is built, as a plugin the operator already has installed.
    private static async Task _InstallLocallyAsync(string stateRoot, string bundled, string id, string directory)
    {
        var plugin = Directory.CreateDirectory(Path.Combine(bundled, id)).FullName;
        foreach (var file in Directory.EnumerateFiles(ServerJourney._Metadata(directory)))
        {
            File.Copy(file, Path.Combine(plugin, Path.GetFileName(file)));
        }

        Directory.CreateDirectory(stateRoot);
        Environment.SetEnvironmentVariable(CockpitBuild.StateRootVariable, stateRoot);
        Assert.Equal([id], await new BundledPluginInstaller().InstallAsync(bundled, PluginBootstrap.PluginsRoot));
    }

    // The server writes its state while the journey reads it; a shared read waits for no one.
    private static async Task<string> _ReadSharedAsync(string path)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(file);
        return await reader.ReadToEndAsync();
    }

    private static bool _HoldsApiToken(Window window) => window.GetLogicalDescendants().OfType<TextBox>().Any(box => box.PlaceholderText == "API token");

    private static TextBox _Box(Control within, string placeholder) =>
        within.GetLogicalDescendants().OfType<TextBox>().Single(box => box.PlaceholderText == placeholder);

    private static void _Click(Control within, string label) =>
        within.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, label)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static bool _IsDepotView(Window window) => _DepotRows(window).Count > 0;

    private static List<Control> _DepotRows(Window window) =>
        [.. window.GetLogicalDescendants().OfType<Control>().Where(control => control.GetType().Name == "DepotConnectionRowControl")];

    // The row's status line: the text beside its Sign in button.
    private static TextBlock _DepotStatus(Control row) =>
        row.GetLogicalDescendants().OfType<Grid>().First(grid => grid.Children.OfType<Button>().Any(button => Equals(button.Content, "Sign in")))
            .Children.OfType<TextBlock>().Single();

    private const string DepotTokenBox = "Installation token (instead of the browser)";

    private static void _FillDepotRow(Control row, string name, string url, string installToken)
    {
        _Box(row, "Name (e.g. Work, Personal)").Text = name;
        _Box(row, "https://depot.example.com").Text = url;
        _Box(row, DepotTokenBox).Text = installToken;
    }

    // Presses one of a Depot row's sign-in buttons, returning the sentence the row answers with.
    private static async Task<string> _PressDepotAsync(Control row, string button)
    {
        var status = _DepotStatus(row);
        var before = status.Text;
        _Click(row, button);
        await Until.Holds(status, () => status.Text is { Length: > 0 } text && text != before && text is not ("Saving…" or "Signing in…"));
        return status.Text ?? "";
    }

    // What the echo fixture's backend part on the server reads of its settings, over its own channel action.
    private static async Task<(string? Greeting, bool ApiTokenSet)> _ReadEchoSettingsAsync(BackendApiClient setup)
    {
        var read = await setup.SendAsync<JsonObject>(HttpMethod.Post, $"api/v1/plugins/{StorePlugin}/channel/read-settings", new { });
        return (read["greeting"]?.GetValue<string>(), read["apiTokenSet"]?.GetValue<bool>() ?? false);
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

    // AC-1534/AC-1585: the echo provider's own profile panel, as the fresh copy of its UI part built it for the server.
    private static TextBox _EchoField(EditableProfileViewModel profile, string name) =>
        (profile.PluginConfigView?.View as Panel)?.Children.OfType<TextBox>().SingleOrDefault(box => box.Name == name)
            ?? throw new InvalidOperationException("The echo provider's own view did not open.");
}

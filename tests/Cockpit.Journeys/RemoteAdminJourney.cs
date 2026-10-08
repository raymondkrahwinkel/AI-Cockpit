using System.Diagnostics;
using System.Net;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.ViewTests;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.BackendApi;

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
        var (fingerprint, _, _) = await ServerJourney._PrepareStateRootAsync(stateRoot, 0, root, "http://127.0.0.1:9/webhook", pluginStore: store);
        var run = ServerJourney._RunServer(
            ServerJourney._Metadata("CockpitServerDirectory"), stateRoot, ServerJourney._Secret(root, "connect-key", bootstrap),
            new Dictionary<string, string> { ["COCKPIT_ALLOW_FILE_CLONES"] = "1" });
        try
        {
            await run.Running.WaitAsync(Until.Ceiling);
            var port = ServerJourney._McpPort(run.Output);
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
            await HeadlessAvalonia.RunAsync(() => admin.InstallPluginCommand.ExecuteAsync(null));
            Assert.True(admin.Plugins.Any(plugin => plugin.Id == "journey-store-plugin"), admin.Status);
            var installed = await setup.GetAsync<JsonArray>("api/v1/plugins");
            Assert.Contains(installed, plugin => plugin?["id"]?.GetValue<string>() == "journey-store-plugin");
            Assert.Contains("restarts", admin.Status, StringComparison.Ordinal);

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
                paneId = group.Sessions.Single().Handle.PaneId;
            });
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
            Assert.Equal("", profilesStatus);
            Assert.Contains("model echo-large", echoed.Data.GetRawText(), StringComparison.Ordinal);
            Assert.Equal(("Running", ""), assistantState);
            Assert.Contains((Laptop, "assistant profile copied from Echo"), assistantAudit);
            Assert.Contains((Laptop, "assistant turned on"), assistantAudit);
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

    private static PluginStoreConfig _CreateFixtureStore(string root)
    {
        var store = Directory.CreateDirectory(Path.Combine(root, "fixture-store")).FullName;
        var zip = Path.Combine(store, "journey-store-plugin-1.0.0.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var file in Directory.EnumerateFiles(ServerJourney._Metadata("EchoProviderDirectory")))
            {
                if (Path.GetFileName(file) == "plugin.json")
                {
                    continue;
                }

                var entry = archive.CreateEntry(Path.GetFileName(file));
                using var source = File.OpenRead(file);
                using var destination = entry.Open();
                source.CopyTo(destination);
            }

            var manifest = archive.CreateEntry("plugin.json");
            using var writer = new StreamWriter(manifest.Open());
            writer.Write("""{"id":"journey-store-plugin","name":"Journey store plugin","version":"1.0.0","entryAssembly":"Cockpit.Plugin.EchoProvider.dll","abstractionsVersion":3}""");
        }

        var hash = PluginHash.Compute(File.ReadAllBytes(zip));
        File.WriteAllText(Path.Combine(store, "index.json"), $$"""{"name":"Journey store","plugins":[{"id":"journey-store-plugin","name":"Journey store plugin","description":null,"author":null,"latestVersion":"1.0.0","versions":[{"version":"1.0.0","path":"journey-store-plugin-1.0.0.zip","abstractionsVersion":3,"minHostVersion":null,"sha256":"{{hash}}","notes":null}]}]}""");
        return PluginStoreConfig.Local(store);
    }

}

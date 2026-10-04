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

// J10 (AC-1446), Admin on a server: the desktop holds an admin key to Cockpit.Server and opens Options there. F5.6b2–b4
// add their steps (projects, profiles, plugins) to this route.
[Collection(JourneyCollection.Alone)]
public sealed class RemoteAdminJourney
{
    private const string Server = "journey-server";

    private const string Laptop = "journey-laptop";

    private const string Phone = "journey-phone";

    // Admin → Connect keys → issue; the new key connects; revoke; the revoked key is refused; a locked-out address is
    // lifted. Each step is in the Audit log with the key that called as its actor, never the node tools' identity.
    [Fact]
    public async Task AnAdminKey_IssuesAKeyThatConnects_RevokesIt_AndLiftsALockout_EachInTheAuditUnderItsOwnLabel()
    {
        var root = Directory.CreateTempSubdirectory("journey-admin-").FullName;
        var stateRoot = Path.Combine(root, "state");
        var store = _CreateFixtureStore(root);
        var bootstrap = _NewKey();
        var (fingerprint, _, _) = await ServerJourney._PrepareStateRootAsync(stateRoot, 0, root, "http://127.0.0.1:9/webhook", pluginStore: store);
        var run = ServerJourney._RunServer(
            ServerJourney._Metadata("CockpitServerDirectory"), stateRoot, Path.Combine(root, "unlock"), ServerJourney._Secret(root, "connect-key", bootstrap));
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

            await HeadlessAvalonia.RunAsync(() =>
            {
                dialog.SelectCategory("server-plugins");
                return Task.CompletedTask;
            });
            Assert.Single(dialog.GetVisualDescendants().OfType<ServerPluginsPage>());
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
            var audit = HeadlessAvalonia.Run(() => admin.Audit.Select(row => (row.Key, row.What)).ToList());
            var lockoutsAfter = HeadlessAvalonia.Run(() => admin.Lockouts.Count);
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

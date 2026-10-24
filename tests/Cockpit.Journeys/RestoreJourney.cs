using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.ViewTests;
using Cockpit.Core.Abstractions.Backup;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Backup;
using Cockpit.Core.Layout;
using Cockpit.Infrastructure.Backup;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions.Backup;
using Cockpit.TestSupport;

namespace Cockpit.Journeys;

// AC-1503: a restore ends in a restart that says which sign-ins came back empty. AC-1508: a backup to a Depot account
// space, through the bundled plugin, comes back as the cockpit it was made of. AC-1510: the same kind of account space
// carries one machine's settings to another.
[Collection(JourneyCollection.Alone)]
public sealed class RestoreJourney
{
    private const string SeededSettings = """{"Plugins":{"depot":{"Enabled":true,"Data":{"connections":"{\"token\":\"journey-secret\"}"}}}}""";

    [Fact]
    public async Task AfterARestoreAndARestart_TheEmptiedSignInsAreAskedFor_AndADepotAccountCarriesBackupsAndSettingsBetweenMachines()
    {
        var work = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"journey-restore-{Guid.NewGuid():N}")).FullName;
        try
        {
            await Step.Run("a restore from a file asks for the emptied sign-ins only", async () =>
            {
                var stripped = await _BackUpAsync(work, "stripped", includeCredentials: false);
                var whole = await _BackUpAsync(work, "whole", includeCredentials: true);

                var asked = await _RestoreAndRestartAsync(work, "stripped-target", (stripped, ["depot"]));
                Assert.Contains(asked, message => message.Contains("new sign-in", StringComparison.Ordinal) && message.Contains("depot", StringComparison.OrdinalIgnoreCase));

                // A plugin left out of the restore keeps the registration it had, so nothing of its was emptied.
                Assert.Empty(await _RestoreAndRestartAsync(work, "left-out-target", (stripped, [])));

                // The last restore has the last word: one that asks nothing also clears what an earlier one left for the restart.
                Assert.Empty(await _RestoreAndRestartAsync(work, "whole-target", (stripped, ["depot"]), (whole, ["depot"])));
            });

            await Step.Run("a backup to a Depot account space restores whole", () => _BackUpToDepotAndRestoreAsync(work));
            await Step.Run("the account space carries settings between two machines", () => _SyncSettingsBetweenTwoMachinesAsync(work));
        }
        finally
        {
            JourneyHost.RemoveStateRoot(work);
        }
    }

    // AC-1510: two machines, each a state root of its own, through the bundled Depot plugin against a fake account space.
    // A's theme reaches B at its next start, and nothing the white list does not carry comes along (acceptance 1); a change
    // made while Depot cannot be reached is said once and arrives when it can again (acceptance 3).
    private static async Task _SyncSettingsBetweenTwoMachinesAsync(string work)
    {
        var space = new _ProfileSpace();
        await using var depot = await LoopbackHttpServer.StartAsync(space.HandleAsync);
        var url = depot.BaseUrl.TrimEnd('/');
        await using (var a = JourneyHost.Desktop(stateRoot: Directory.CreateDirectory(Path.Combine(work, "sync-a")).FullName))
        {
            await _SignedInToProfileAsync(a, url);
            var layout = a.Services.GetRequiredService<ILayoutSettingsStore>();
            await layout.SaveAsync(await layout.LoadAsync() with { ThemeMode = ThemeMode.Light });
            await a.Services.GetRequiredService<SettingsCarrierSync>().RunOnceAsync();
        }

        Assert.Equal("Light", space.Document?["Layout"]?["ThemeMode"]?.GetValue<string>());

        // The other side of the white list: what this build does not carry is never taken from the carrier.
        var document = space.Document ?? throw new InvalidOperationException("A pushed nothing.");
        document["SomeNewSection"] = new JsonObject { ["Token"] = "journey-secret" };
        document["Layout"]?.AsObject().Add("SidebarWidth", 999);
        space.Document = document;

        var b = Directory.CreateDirectory(Path.Combine(work, "sync-b")).FullName;
        await using (var first = JourneyHost.Desktop(stateRoot: b))
        {
            await _SignedInToProfileAsync(first, url);
            await first.Services.GetRequiredService<SettingsCarrierSync>().RunOnceAsync();
            await HeadlessAvalonia.RunAsync(() => Until.CollectionHolds(first.Cockpit.Toasts, () =>
                first.Cockpit.Toasts.Any(toast => toast.Message == "Settings fetched from Depot: Journey; active after a restart.")));
        }

        await using var restarted = JourneyHost.Desktop(stateRoot: b);
        await restarted.StartDesktopAsync();
        var settings = JsonNode.Parse(await File.ReadAllTextAsync(CockpitConfigPath.For(b)));
        Assert.Equal("Light", settings?["Layout"]?["ThemeMode"]?.GetValue<string>());
        await HeadlessAvalonia.RunAsync(() => Until.Holds(restarted.Cockpit, () => restarted.Cockpit.ThemeMode == ThemeMode.Light));
        Assert.Null(settings?["SomeNewSection"]);
        Assert.NotEqual(999, settings?["Layout"]?["SidebarWidth"]?.GetValue<double>());

        // Offline: the change waits, the operator is told once, and it goes up as soon as Depot answers again.
        var sync = restarted.Services.GetRequiredService<SettingsCarrierSync>();
        await sync.RunOnceAsync();
        space.Offline = true;
        var terminal = restarted.Services.GetRequiredService<ITerminalSettingsStore>();
        await terminal.SaveAsync(await terminal.LoadAsync() with { FontSize = 16 });
        await HeadlessAvalonia.RunAsync(() => Until.CollectionHolds(restarted.Cockpit.Toasts, () =>
            restarted.Cockpit.Toasts.Any(toast => toast.Message.StartsWith("Your settings could not be synced with Depot: Journey", StringComparison.Ordinal))));
        Assert.NotEqual(16, space.Document?["Terminal"]?["FontSize"]?.GetValue<int>());

        // Back online, and another machine writes just before this push: Depot answers 412, the plugin says so, and the
        // host reads again and pushes over the new version, keeping what the other machine wrote.
        space.Offline = false;
        space.BeforeNextWrite = other => other["Projects"] = new JsonArray(new JsonObject { ["Id"] = "from-a", ["Name"] = "From A" });
        await sync.RunOnceAsync();
        Assert.Equal((16, "From A"), (space.Document?["Terminal"]?["FontSize"]?.GetValue<int>(), space.Document?["Projects"]?[0]?["Name"]?.GetValue<string>()));
        Assert.Equal(1, space.Refused);
    }

    // A copy Depot did not keep whole is reported and never listed; a whole one restores the setting it was made with.
    private static async Task _BackUpToDepotAndRestoreAsync(string work)
    {
        var space = new _AccountSpace();
        await using var depot = await LoopbackHttpServer.StartAsync(space.HandleAsync);
        var url = depot.BaseUrl.TrimEnd('/');
        string Seed(int sidebar) => "{\"Layout\":{\"SidebarWidth\":" + sidebar + "}}";

        string id;
        await using (var source = JourneyHost.Desktop(root => File.WriteAllText(CockpitConfigPath.For(root), Seed(333)), Directory.CreateDirectory(Path.Combine(work, "depot-source")).FullName))
        {
            var target = await _SignedInDepotAsync(source, url);

            space.Corrupt = true;
            await HeadlessAvalonia.RunAsync(() => source.Cockpit.CreateBackupAsync(target));
            Assert.StartsWith("The backup was not made: The backup did not arrive whole", HeadlessAvalonia.Run(() => source.Cockpit.BackupStatus), StringComparison.Ordinal);
            Assert.Empty(await target.ListAsync());

            space.Corrupt = false;
            await HeadlessAvalonia.RunAsync(() => source.Cockpit.CreateBackupAsync(target));
            Assert.StartsWith("Backed up to Depot: Journey.", HeadlessAvalonia.Run(() => source.Cockpit.BackupStatus), StringComparison.Ordinal);
            id = Assert.Single(await target.ListAsync()).Id;
        }

        var restored = Directory.CreateDirectory(Path.Combine(work, "depot-target")).FullName;
        await using (var fresh = JourneyHost.Desktop(root => File.WriteAllText(CockpitConfigPath.For(root), Seed(222)), restored))
        {
            var target = await _SignedInDepotAsync(fresh, url);
            await using var archive = await target.OpenReadAsync(id);
            var report = await fresh.Services.GetRequiredService<IBackupService>().RestoreAsync(archive, new RestoreOptions(Settings: true, []));
            Assert.False(report.Stopped);
        }

        var settings = JsonNode.Parse(File.ReadAllText(CockpitConfigPath.For(restored)));
        Assert.Equal(333, settings?["Layout"]?["SidebarWidth"]?.GetValue<double>());
    }

    // The bundled Depot plugin with the connection seeded, and the host holding a credential for it as a sign-in leaves one.
    // Seeded after the build: a registration already in cockpit.json would keep the bundled plugin from being installed.
    private static async Task<IBackupTarget> _SignedInDepotAsync(JourneyHost cockpit, string url)
    {
        var connections = JsonSerializer.Serialize(new[] { new { Id = "journey", Name = "Journey", Url = url } });
        await cockpit.Services.GetRequiredService<IPluginRegistrationStore>().SaveDataAsync("depot", new Dictionary<string, string> { ["connections"] = connections });
        await cockpit.Services.GetRequiredService<IMcpOAuthTokenStore>().SaveAsync(
            "journey",
            "Depot: Journey",
            DepotHostScope.SignedIn(_AccountSpace.Bearer, url));
        await cockpit.StartDesktopAsync();

        return cockpit.Services.GetRequiredService<BackupTargets>().Find("depot:journey")
            ?? throw new InvalidOperationException("The Depot plugin registered no backup target for its connection.");
    }

    // Depot's /cockpit/backups (DEP-168) for the one account the bearer names; `Corrupt` stores a damaged copy.
    private sealed class _AccountSpace
    {
        public const string Bearer = "journey-depot-bearer";

        private readonly Dictionary<Guid, byte[]> _stored = [];

        public bool Corrupt { get; set; }

        public async Task HandleAsync(HttpContext context)
        {
            if (await DepotHostScope.TryAnswerAsync(context, Bearer))
            {
                return;
            }

            var request = context.Request;
            if (request.Headers.Authorization != $"Bearer {Bearer}" || !request.Path.StartsWithSegments("/cockpit/backups", out var rest))
            {
                context.Response.StatusCode = request.Path.StartsWithSegments("/cockpit") ? 401 : 404;
                return;
            }

            var id = Guid.TryParse(rest.Value?.Trim('/'), out var parsed) ? parsed : (Guid?)null;
            if (HttpMethods.IsPost(request.Method))
            {
                using var body = new MemoryStream();
                await request.Body.CopyToAsync(body);
                var data = body.ToArray();
                if (Corrupt)
                {
                    data[^1] ^= 0xFF;
                }

                var added = Guid.NewGuid();
                _stored[added] = data;
                context.Response.StatusCode = 201;
                await context.Response.WriteAsJsonAsync(_Info(added, data));
            }
            else if (HttpMethods.IsDelete(request.Method) && id is { } gone)
            {
                context.Response.StatusCode = _stored.Remove(gone) ? 204 : 404;
            }
            else if (id is { } wanted)
            {
                if (_stored.TryGetValue(wanted, out var data))
                {
                    await context.Response.Body.WriteAsync(data);
                }
                else
                {
                    context.Response.StatusCode = 404;
                }
            }
            else
            {
                await context.Response.WriteAsJsonAsync(_stored.Select(pair => _Info(pair.Key, pair.Value)));
            }
        }

        private static object _Info(Guid id, byte[] data) =>
            new { id, size = data.LongLength, sha256 = Convert.ToHexStringLower(SHA256.HashData(data)), createdAt = DateTime.UtcNow };
    }

    private static async Task<string> _BackUpAsync(string work, string name, bool includeCredentials)
    {
        await using var source = JourneyHost.Desktop(root => File.WriteAllText(CockpitConfigPath.For(root), SeededSettings), Directory.CreateDirectory(Path.Combine(work, $"{name}-source")).FullName);
        var archive = Path.Combine(work, $"{name}.zip");
        await source.Services.GetRequiredService<IBackupService>().WriteAsync(archive, new BackupOptions(IncludeCredentials: includeCredentials));

        return archive;
    }

    private static async Task<IReadOnlyList<string>> _RestoreAndRestartAsync(string work, string name, params (string Archive, string[] Plugins)[] restores)
    {
        var target = Directory.CreateDirectory(Path.Combine(work, name)).FullName;

        await using (var before = JourneyHost.Desktop(stateRoot: target))
        {
            var backups = before.Services.GetRequiredService<IBackupService>();

            foreach (var (archive, plugins) in restores)
            {
                await backups.RestoreAsync(archive, new RestoreOptions(Settings: true, plugins));
            }
        }

        await using var after = JourneyHost.Desktop(stateRoot: target);
        await after.StartDesktopAsync();

        return HeadlessAvalonia.Run(() => after.Cockpit.Toasts.Select(toast => toast.Message).ToList());
    }
    // The bundled Depot plugin with one connection and the host holding its sign-in, as _SignedInDepotAsync seeds it.
    private static async Task _SignedInToProfileAsync(JourneyHost cockpit, string url)
    {
        var connections = JsonSerializer.Serialize(new[] { new { Id = "journey", Name = "Journey", Url = url } });
        await cockpit.Services.GetRequiredService<IPluginRegistrationStore>().SaveDataAsync("depot", new Dictionary<string, string> { ["connections"] = connections });
        await cockpit.Services.GetRequiredService<IMcpOAuthTokenStore>().SaveAsync(
            "journey",
            "Depot: Journey",
            DepotHostScope.SignedIn(_ProfileSpace.Bearer, url));
        await cockpit.StartDesktopAsync();
    }

    // Depot's /cockpit/profile (DEP-168) for the one account the bearer names, with If-Match on the SHA-256 of the bytes.
    private sealed class _ProfileSpace
    {
        public const string Bearer = "journey-depot-bearer";

        private byte[]? _stored;

        public bool Offline { get; set; }

        // Another machine's write, landing once just before the next one arrives.
        public Action<JsonObject>? BeforeNextWrite { get; set; }

        // How many writes were refused as stale (412).
        public int Refused { get; private set; }

        public JsonObject? Document
        {
            get => _stored is null ? null : JsonNode.Parse(_stored) as JsonObject;
            set => _stored = value is null ? null : JsonSerializer.SerializeToUtf8Bytes(value);
        }

        public async Task HandleAsync(HttpContext context)
        {
            var request = context.Request;
            if (!Offline && await DepotHostScope.TryAnswerAsync(context, Bearer))
            {
                return;
            }

            if (Offline)
            {
                context.Response.StatusCode = 503;
                return;
            }

            if (request.Headers.Authorization != $"Bearer {Bearer}" || request.Path != "/cockpit/profile")
            {
                context.Response.StatusCode = request.Path.StartsWithSegments("/cockpit") ? 401 : 404;
                return;
            }

            var current = _stored is null ? null : Convert.ToHexStringLower(SHA256.HashData(_stored));
            if (HttpMethods.IsGet(request.Method))
            {
                if (_stored is null)
                {
                    context.Response.StatusCode = 204;
                    return;
                }

                await context.Response.Body.WriteAsync(_stored);
                return;
            }

            if (BeforeNextWrite is { } other && Document is { } document)
            {
                BeforeNextWrite = null;
                other(document);
                Document = document;
                current = Convert.ToHexStringLower(SHA256.HashData(_stored ?? []));
            }

            var expected = request.Headers.IfMatch.FirstOrDefault()?.Trim('"');
            var createOnly = request.Headers.IfNoneMatch == "*";
            if (createOnly ? current is not null : expected is null || expected != current)
            {
                Refused += expected is null && !createOnly ? 0 : 1;
                context.Response.StatusCode = expected is null && !createOnly ? 428 : 412;
                return;
            }

            using var body = new MemoryStream();
            await request.Body.CopyToAsync(body);
            _stored = body.ToArray();
        }
    }
}

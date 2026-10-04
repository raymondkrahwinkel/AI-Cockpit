using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.App;
using Cockpit.App.ViewTests;
using Cockpit.Core.Configuration;
using Cockpit.Core.Mcp;
using Cockpit.Core.Secrets;
using Cockpit.Infrastructure.BackendApi;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Journeys;

// AC-1487, `Cockpit --remote <server>`: the route Program runs, short of Avalonia's run, over a real Cockpit.Server. It
// unlocks an encrypted registry in memory, shows the server's sessions, writes nothing local and opens no port.
[Collection(JourneyCollection.Alone)]
public sealed class RemoteWindowJourney
{
    private const string Server = "journey-server";

    [Fact]
    public async Task ARemoteWindow_UnlocksAndShowsTheServersSessions_LeavesLocalRootsByteEqual_AndOpensNoPort()
    {
        var root = Directory.CreateTempSubdirectory("journey-remote-window-").FullName;
        var localRoot = Path.Combine(root, "local");
        var damagedRoot = Directory.CreateDirectory(Path.Combine(root, "damaged")).FullName;
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var key = "ck_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var (fingerprint, _, _) = await ServerJourney._PrepareStateRootAsync(Path.Combine(root, "server"), 0, root, "http://127.0.0.1:9/webhook", withTerminalProfile: false);
        var run = ServerJourney._RunServer(
            ServerJourney._Metadata("CockpitServerDirectory"), Path.Combine(root, "server"), ServerJourney._Secret(root, "connect-key", key));
        var previousRoot = Environment.GetEnvironmentVariable(CockpitBuild.StateRootVariable);
        try
        {
            await run.Running.WaitAsync(Until.Ceiling);
            var port = ServerJourney._McpPort(run.Output);
            using var admin = new BackendApiClient(new Uri($"https://127.0.0.1:{port}/"), key, fingerprint, TimeProvider.System);
            var started = await admin.SendAsync<JsonObject>(HttpMethod.Post, "api/v1/sessions", new { profile = "Echo", prompt = "hello" });
            var paneId = started["paneId"]?.GetValue<string>() ?? "";
            await new McpServerStore(CockpitConfigPath.For(localRoot)).SaveAsync(
            [
                new McpServerConfig
                {
                    Id = McpServerIdentity.NewId(),
                    Name = NodeServerName.For(Server, NodeServerName.SessionsServerName),
                    Transport = McpTransport.Http,
                    Scope = McpServerScope.LocalOnly,
                    Url = $"https://127.0.0.1:{port}/mcp",
                    Auth = McpServerAuth.ApiKey,
                    ApiKey = key,
                    PinnedCertificateFingerprint = fingerprint,
                },
            ]);
            await new SecretProtectionService(CockpitConfigPath.For(localRoot), new SecretKeyHolder()).EnableAsync(password);

            // A live file that does not parse beside a good backup: the desktop's loader would quarantine and restore it.
            File.WriteAllText(CockpitConfigPath.For(damagedRoot), "{ \"mcpServers\": [");
            File.Copy(CockpitConfigPath.For(localRoot), CockpitConfigPath.For(damagedRoot) + ".bak");

            var localBefore = _Bytes(localRoot);
            var damagedBefore = _Bytes(damagedRoot);
            var listenersBefore = _OwnListeners();
            IReadOnlySet<string> listenersWhileShown = new HashSet<string>();
            IReadOnlyList<string> shown = [];
            var lockedAtStart = false;
            var damagedRefusal = "";
            await HeadlessAvalonia.RunAsync(async () =>
            {
                // As the operator's own process starts: COCKPIT_STATE_ROOT is the local root until RemoteStartup moves it.
                Environment.SetEnvironmentVariable(CockpitBuild.StateRootVariable, localRoot);
                using var startup = RemoteStartup.Begin(Server, _ => NullLoggerFactory.Instance)
                    ?? throw new InvalidOperationException("Another remote window held this server's claim.");
                lockedAtStart = startup.NeedsUnlock;
                await (startup.Protection?.UnlockAsync(password) ?? Task.FromResult(false));
                var view = startup.Open(_ => Task.CompletedTask)
                    ?? throw new InvalidOperationException($"The remote window did not open: {startup.Refusal}");
                await Until.CollectionHolds(view.ServerGroups, () => view.ServerGroups.Count == 1);
                var group = view.ServerGroups[0];
                await Until.Holds(group, () => group.IsConnected);
                await Until.CollectionHolds(group.Sessions, () => group.Sessions.Count > 0);
                shown = [.. group.Sessions.Select(row => row.Handle.PaneId)];
                listenersWhileShown = _OwnListeners();

                Environment.SetEnvironmentVariable(CockpitBuild.StateRootVariable, damagedRoot);
                using var damaged = RemoteStartup.Begin(Server, _ => NullLoggerFactory.Instance)
                    ?? throw new InvalidOperationException("Another remote window held this server's claim.");
                damagedRefusal = damaged.Refusal ?? "";
            });

            Assert.True(lockedAtStart);
            Assert.Equal([paneId], shown);
            Assert.Equal(localBefore, _Bytes(localRoot));
            Assert.Empty(listenersWhileShown.Except(listenersBefore));
            Assert.Contains("Open the local Cockpit first", damagedRefusal, StringComparison.Ordinal);
            Assert.Equal(damagedBefore, _Bytes(damagedRoot));
        }
        finally
        {
            Environment.SetEnvironmentVariable(CockpitBuild.StateRootVariable, previousRoot);
            if (!run.Process.HasExited)
            {
                run.Process.Kill(entireProcessTree: true);
            }

            run.Process.Dispose();
            JourneyHost.RemoveStateRoot(root);
        }
    }

    // Every entry under `root` with a hash of its bytes, so a write, a new file and a new folder all show.
    private static IReadOnlyList<string> _Bytes(string root) =>
    [
        .. Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(path => $"{Path.GetRelativePath(root, path)} {(File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "dir")}")
            .Order(StringComparer.Ordinal),
    ];

    // The TCP listeners and bound UDP sockets of this process, which runs the remote route: /proc on Linux, netstat on Windows.
    private static IReadOnlySet<string> _OwnListeners() => OperatingSystem.IsLinux() ? _ProcListeners() : _NetstatListeners();

    private static IReadOnlySet<string> _ProcListeners()
    {
        var sockets = Directory.EnumerateFiles("/proc/self/fd")
            .Select(fd => new FileInfo(fd).LinkTarget)
            .OfType<string>()
            .Where(target => target.StartsWith("socket:[", StringComparison.Ordinal))
            .Select(target => target[8..^1])
            .ToHashSet(StringComparer.Ordinal);
        return new[] { (Name: "tcp", Bound: "0A"), (Name: "tcp6", Bound: "0A"), (Name: "udp", Bound: "07"), (Name: "udp6", Bound: "07") }
            .SelectMany(table => File.ReadLines($"/proc/self/net/{table.Name}").Skip(1)
                .Select(line => (table.Name, table.Bound, Columns: line.Split(' ', StringSplitOptions.RemoveEmptyEntries))))
            .Where(entry => entry.Columns[3] == entry.Bound && sockets.Contains(entry.Columns[9]))
            .Select(entry => $"{entry.Name} {entry.Columns[1]}")
            .ToHashSet(StringComparer.Ordinal);
    }

    // A listener is the row with no remote end; read that way rather than by its state word, which netstat localizes.
    private static IReadOnlySet<string> _NetstatListeners()
    {
        using var netstat = Process.Start(new ProcessStartInfo("netstat", "-ano") { RedirectStandardOutput = true })
            ?? throw new InvalidOperationException("netstat did not start.");
        var pid = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var listeners = netstat.StandardOutput.ReadToEnd()
            .Split('\n')
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(columns => (columns is ["TCP", _, "0.0.0.0:0" or "[::]:0", _, var owner] && owner.Trim() == pid)
                              || (columns is ["UDP", _, "*:*", var udpOwner] && udpOwner.Trim() == pid))
            .Select(columns => $"{columns[0]} {columns[1]}")
            .ToHashSet(StringComparer.Ordinal);
        netstat.WaitForExit();
        return listeners;
    }
}

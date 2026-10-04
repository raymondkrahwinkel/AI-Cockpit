using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.App;
using Cockpit.App.ViewTests;
using Cockpit.Core.Configuration;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.BackendApi;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Journeys;

// AC-1487, `Cockpit --remote <server>`: the window as Program composes it, over a real Cockpit.Server. It shows the
// server's sessions, and it neither writes to the operator's root nor opens a port of its own.
[Collection(JourneyCollection.Alone)]
public sealed class RemoteWindowJourney
{
    private const string Server = "journey-server";

    [Fact]
    public async Task ARemoteWindow_ShowsTheServersSessions_LeavesTheLocalRootByteEqual_AndOpensNoPort()
    {
        var root = Directory.CreateTempSubdirectory("journey-remote-window-").FullName;
        var localRoot = Path.Combine(root, "local");
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

            // As Program does it: the root moves off the local one first, then the window is composed.
            var localBefore = _Bytes(localRoot);
            var listenersBefore = _OwnListeners();
            IReadOnlySet<string> listenersWhileShown = new HashSet<string>();
            IReadOnlyList<string> shown = [];
            Environment.SetEnvironmentVariable(CockpitBuild.StateRootVariable, RemoteInstance.StateRootFor(localRoot, Server));
            await HeadlessAvalonia.RunAsync(async () =>
            {
                var remote = await Program.ComposeRemoteWindowAsync(localRoot, Server, NullLoggerFactory.Instance, _ => Task.CompletedTask)
                    ?? throw new InvalidOperationException("The local registry's row composed no remote window.");
                await using var connection = remote.Connection;
                var view = remote.Cockpit;
                await Until.CollectionHolds(view.ServerGroups, () => view.ServerGroups.Count == 1);
                var group = view.ServerGroups[0];
                await Until.Holds(group, () => group.IsConnected);
                await Until.CollectionHolds(group.Sessions, () => group.Sessions.Count > 0);
                shown = [.. group.Sessions.Select(row => row.Handle.PaneId)];
                listenersWhileShown = _OwnListeners();
            });

            Assert.Equal([paneId], shown);
            Assert.Equal(localBefore, _Bytes(localRoot));
            Assert.Empty(listenersWhileShown.Except(listenersBefore));
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

    // The TCP ports this process listens on, as the OS lists them per process: /proc on Linux, netstat on Windows.
    private static IReadOnlySet<string> _OwnListeners() => OperatingSystem.IsLinux() ? _ProcListeners() : _NetstatListeners();

    private static IReadOnlySet<string> _ProcListeners()
    {
        var sockets = Directory.EnumerateFiles("/proc/self/fd")
            .Select(fd => new FileInfo(fd).LinkTarget)
            .OfType<string>()
            .Where(target => target.StartsWith("socket:[", StringComparison.Ordinal))
            .Select(target => target[8..^1])
            .ToHashSet(StringComparer.Ordinal);
        return new[] { "/proc/self/net/tcp", "/proc/self/net/tcp6" }
            .SelectMany(table => File.ReadLines(table).Skip(1))
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(columns => columns[3] == "0A" && sockets.Contains(columns[9]))
            .Select(columns => columns[1])
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
            .Where(columns => columns is ["TCP", _, "0.0.0.0:0" or "[::]:0", _, var owner] && owner.Trim() == pid)
            .Select(columns => columns[1])
            .ToHashSet(StringComparer.Ordinal);
        netstat.WaitForExit();
        return listeners;
    }
}

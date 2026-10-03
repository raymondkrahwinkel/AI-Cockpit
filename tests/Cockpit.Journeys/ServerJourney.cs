using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Secrets;
using Cockpit.Core.Abstractions.Workspaces;
using Cockpit.Core.Configuration;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Core.Workspaces;
using Cockpit.Infrastructure.BackendApi;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Infrastructure.Hosting;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Plugins;

namespace Cockpit.Journeys;

// J8, the server as a container runs it: Cockpit.Server as a process of its own on a fresh state root, unlocked from a
// file, reached with a connect key, its sessions from the echo fixture plugin. Secrets travel as files in a temp folder.
[Collection(JourneyCollection.Alone)]
public sealed class ServerJourney
{
    private const string RunningLine = "Cockpit.Server running; UI assemblies loaded: ";

    // What a `docker stop` waits before it kills.
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task TheServer_StartsOnlyWithItsUnlockFile_RunsAnSdkSession_RefusesTty_AndStopsOnSigterm()
    {
        var serverDirectory = _Metadata("CockpitServerDirectory");
        Assert.Empty(Directory.EnumerateFiles(serverDirectory, "Avalonia*.dll", SearchOption.AllDirectories).Select(Path.GetFileName));

        var root = Directory.CreateTempSubdirectory("journey-server-").FullName;
        var stateRoot = Path.Combine(root, "state");
        var key = "ck_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var fingerprint = await _PrepareStateRootAsync(stateRoot, 0, root);
        var keyFile = _Secret(root, "connect-key", key);
        string[] secrets = [File.ReadAllText(Path.Combine(root, "unlock")), key];
        Process? server = null;
        try
        {
            // A wrong password: the server does not start, and says why.
            var refusedRun = _RunServer(serverDirectory, stateRoot, _Secret(root, "wrong-unlock", "not-the-password"), keyFile);
            Assert.True(refusedRun.Process.WaitForExit(Until.Ceiling), "The server with a wrong unlock file kept running.");
            refusedRun.Process.WaitForExit();
            Assert.NotEqual(0, refusedRun.Process.ExitCode);
            Assert.Contains("The password in the unlock password file is not correct.", refusedRun.Output, StringComparison.Ordinal);
            _AssertCarriesNoSecret(refusedRun, secrets);
            refusedRun.Process.Dispose();

            // The right one: it runs, with no UI assembly loaded.
            var run = _RunServer(serverDirectory, stateRoot, Path.Combine(root, "unlock"), keyFile);
            server = run.Process;
            Assert.Equal($"{RunningLine}none.", await run.Running.WaitAsync(Until.Ceiling));

            using var admin = new BackendApiClient(new Uri($"https://127.0.0.1:{_McpPort(run.Output)}/"), key, fingerprint, TimeProvider.System);
            using var timeout = new CancellationTokenSource(Until.Ceiling);
            var started = await admin.SendAsync<JsonObject>(HttpMethod.Post, "api/v1/sessions", new { profile = "Echo", prompt = "hello" });
            var paneId = started["paneId"]?.GetValue<string>() ?? "";
            var answer = await admin.StreamEventsAsync(0, timeout.Token)
                .FirstAsync(evt => evt.Kind == "row" && evt.Data.GetRawText().Contains(paneId, StringComparison.Ordinal) && evt.Data.GetRawText().Contains("echo: hello", StringComparison.Ordinal), timeout.Token);
            var cli = int.Parse(Regex.Match(answer.Data.GetRawText(), @"\(cli (\d+)\)").Groups[1].Value);

            var refusedTty = await Assert.ThrowsAsync<BackendApiException>(() => admin.SendAsync<JsonObject>(HttpMethod.Post, "api/v1/sessions", new { profile = "Terminal" }));
            Assert.Equal(HttpStatusCode.Conflict, refusedTty.Status);
            Assert.Contains("TTY session", refusedTty.Description, StringComparison.Ordinal);

            // SIGTERM has no Windows counterpart a test can send to another process; there the cleanup below ends it.
            if (!OperatingSystem.IsWindows())
            {
                var clock = Stopwatch.StartNew();
                using (var signal = Process.Start("kill", ["-TERM", server.Id.ToString()]) ?? throw new InvalidOperationException("kill did not start."))
                {
                    await signal.WaitForExitAsync();
                }

                Assert.True(server.WaitForExit(StopGrace), "The server did not stop within a docker stop's grace.");
                server.WaitForExit();
                Assert.True(clock.Elapsed < StopGrace, $"The stop took {clock.Elapsed}.");
                Assert.Equal(0, server.ExitCode);
                Assert.Contains("Cockpit.Server stopped.", run.Output, StringComparison.Ordinal);
                Assert.False(_IsRunning(cli), $"The session's process {cli} outlived the server.");
            }

            _AssertCarriesNoSecret(run, secrets);
        }
        finally
        {
            if (server is { HasExited: false })
            {
                server.Kill(entireProcessTree: true);
            }

            server?.Dispose();
            JourneyHost.RemoveStateRoot(root);
        }
    }

    // What the operator set up before: a desk, an SDK and a TTY profile, the node door on `port`, encrypted credentials
    // and the echo plugin installed. Written by the backend's own stores, in-process; returns the node's fingerprint.
    private static async Task<string> _PrepareStateRootAsync(string stateRoot, int port, string root)
    {
        var previous = Environment.GetEnvironmentVariable(CockpitBuild.StateRootVariable);
        Environment.SetEnvironmentVariable(CockpitBuild.StateRootVariable, stateRoot);
        try
        {
            var backend = CockpitBackend.Build(NullLoggerFactory.Instance);
            await using var services = backend.Services;
            var desk = Workspace.Create("Sessions", WorkspaceType.Sessions);
            await services.GetRequiredService<IWorkspaceSettingsStore>().SaveAsync(new WorkspaceSettings { Workspaces = [desk], ActiveWorkspaceId = desk.Id });
            await services.GetRequiredService<ISessionProfileStore>().SaveAsync(
            [
                new SessionProfile("Echo", new PluginProviderConfig("echo-provider.echo", "{}")) { DefaultKind = ProfileSessionKind.Sdk },
                new SessionProfile("Terminal", new ClaudeConfig(Path.Combine(stateRoot, ".claude"))) { DefaultKind = ProfileSessionKind.Tty },
            ]);
            await services.GetRequiredService<INodeEndpointSettingsStore>().SaveAsync(new NodeEndpointSettings { Enabled = true, SharedSecret = Guid.NewGuid().ToString("N"), Port = port });

            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            await services.GetRequiredService<ISecretProtectionService>().EnableAsync(password);
            _Secret(root, "unlock", password);

            var plugin = Directory.CreateDirectory(Path.Combine(root, "bundled", "echo-provider")).FullName;
            foreach (var file in Directory.EnumerateFiles(_Metadata("EchoProviderDirectory")))
            {
                File.Copy(file, Path.Combine(plugin, Path.GetFileName(file)));
            }

            Assert.Equal("echo-provider", Assert.Single(await new BundledPluginInstaller().InstallAsync(Path.Combine(root, "bundled"), PluginBootstrap.PluginsRoot)));
            return services.GetRequiredService<NodeSelfSignedCertificate>().Fingerprint;
        }
        finally
        {
            Environment.SetEnvironmentVariable(CockpitBuild.StateRootVariable, previous);
        }
    }

    private static ServerRun _RunServer(string serverDirectory, string stateRoot, string unlockFile, string keyFile)
    {
        var start = new ProcessStartInfo(Path.Combine(serverDirectory, OperatingSystem.IsWindows() ? "Cockpit.Server.exe" : "Cockpit.Server"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = serverDirectory,
        };
        start.Environment[CockpitBuild.StateRootVariable] = stateRoot;
        start.Environment[UnlockFromFile.Variable] = unlockFile;
        start.Environment[ConnectKeyVerifier.BootstrapFileVariable] = keyFile;
        var run = new ServerRun(new Process { StartInfo = start, EnableRaisingEvents = true });
        run.Process.Start();
        run.Process.BeginOutputReadLine();
        run.Process.BeginErrorReadLine();
        return run;
    }

    // Redaction is a second layer; without this, a later log line with a raw secret stays green. The message names none.
    private static void _AssertCarriesNoSecret(ServerRun run, string[] secrets) =>
        Assert.False(secrets.Any(secret => run.Output.Contains(secret, StringComparison.Ordinal)), "The server's output carries the unlock password or the connect key.");

    private static string _Secret(string root, string name, string value)
    {
        var path = Path.Combine(root, name);
        File.WriteAllText(path, value);
        return path;
    }

    private static int _McpPort(string output)
    {
        var match = Regex.Match(output, @"Now listening on: https://0\.0\.0\.0:(\d+)");
        return match.Success
            ? int.Parse(match.Groups[1].Value)
            : throw new InvalidOperationException($"Cockpit.Server did not report its node listener:\n{output}");
    }

    private static bool _IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string _Metadata(string key) =>
        typeof(ServerJourney).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(attribute => attribute.Key == key).Value
        ?? throw new InvalidOperationException($"The journeys were built without {key}.");

    // The server's console, collected; `Running` completes with the line that says it is up.
    private sealed class ServerRun
    {
        private readonly List<string> _lines = [];
        private readonly TaskCompletionSource<string> _running = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ServerRun(Process process)
        {
            Process = process;
            process.OutputDataReceived += (_, line) => _Collect(line.Data);
            process.ErrorDataReceived += (_, line) => _Collect(line.Data);
            process.Exited += (_, _) => _running.TrySetException(new InvalidOperationException($"Cockpit.Server exited before it ran:\n{Output}"));
        }

        public Process Process { get; }

        public Task<string> Running => _running.Task;

        public string Output
        {
            get
            {
                lock (_lines)
                {
                    return string.Join("\n", _lines);
                }
            }
        }

        private void _Collect(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (_lines)
            {
                _lines.Add(line);
            }

            var at = line.IndexOf(RunningLine, StringComparison.Ordinal);
            if (at >= 0)
            {
                _running.TrySetResult(line[at..]);
            }
        }
    }
}

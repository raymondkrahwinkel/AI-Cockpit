using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Secrets;
using Cockpit.Core.Abstractions.Workspaces;
using Cockpit.Core.Configuration;
using Cockpit.Core.Mcp;
using Cockpit.Core.Notifications;
using Cockpit.Core.Profiles;
using Cockpit.Core.Secrets;
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

    // While this file exists the EchoSignIn profile reads as signed in.
    private const string SignedInFile = "echo-signed-in";

    // The Discord lines for that profile. Other profiles may alarm too: the Claude plugin's cached check reads a
    // profile without credentials as expired a poll after it first guessed it signed in.
    private const string Expired = "The sign-in of profile 'EchoSignIn'";

    private const string Restored = "Profile 'EchoSignIn' on";

    // The session lines, as the desktop words them.
    private const string NeedsAttention = "** — Needs attention";

    private const string Done = "** — Done";

    // What a `docker stop` waits before it kills.
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(10);

    // AC-1357: the sign-in poll, a second here instead of five minutes, so three polls fit in a few seconds.
    private static readonly TimeSpan LoginCheckInterval = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task TheServer_StartsOnlyWithItsUnlockFile_RunsAnSdkSession_RefusesTty_AlarmsOnceOnAnExpiredSignIn_AnswersHealthzWithoutAKey_AndStopsOnSigterm()
    {
        var serverDirectory = _Metadata("CockpitServerDirectory");
        Assert.Empty(Directory.EnumerateFiles(serverDirectory, "Avalonia*.dll", SearchOption.AllDirectories).Select(Path.GetFileName));

        var root = Directory.CreateTempSubdirectory("journey-server-").FullName;
        var stateRoot = Path.Combine(root, "state");
        var key = "ck_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        await using var discord = await _FakeDiscordAsync();
        var (fingerprint, controllerKey) = await _PrepareStateRootAsync(stateRoot, 0, root, discord.Url);
        var keyFile = _Secret(root, "connect-key", key);
        string[] secrets = [File.ReadAllText(Path.Combine(root, "unlock")), key, controllerKey];
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

            // AC-1467: the finished hello session is said once, and a session stopped on a permission once too.
            await admin.SendAsync<JsonObject>(HttpMethod.Post, "api/v1/sessions", new { profile = "Echo", prompt = "ask" });
            await discord.WaitForAsync(NeedsAttention, 1, timeout.Token);
            await discord.WaitForAsync(Done, 1, timeout.Token);
            Assert.Equal((1, 1), (discord.Count(NeedsAttention), discord.Count(Done)));

            // AC-1357: a sign-in that expires is said once, to Discord and to the controller; three more polls stay
            // quiet, and its return is said once too. A controller key's first call makes it the controller.
            await using var controller = await _ControllerAsync(_McpPort(run.Output), controllerKey, fingerprint);
            await _ReadNodeInboxAsync(controller);
            File.Delete(Path.Combine(root, SignedInFile));
            await discord.WaitForAsync(Expired, 1, timeout.Token);
            await Task.Delay(3 * LoginCheckInterval, timeout.Token);
            Assert.Equal(1, discord.Count(Expired));
            File.WriteAllText(Path.Combine(root, SignedInFile), "");
            await discord.WaitForAsync(Restored, 1, timeout.Token);
            Assert.Equal(1, discord.Count(Expired));
            var inbox = await _ReadNodeInboxAsync(controller);
            Assert.Equal(1, inbox.Count(message => message.Kind == "login-expired" && message.Body.Contains("'EchoSignIn'", StringComparison.Ordinal)));
            Assert.Equal(1, inbox.Count(message => message.Kind == "login-restored" && message.Body.Contains("'EchoSignIn'", StringComparison.Ordinal)));

            // AC-1466: Docker's probe needs no key and hears only that the Workflows scheduler holds; the API does not
            // answer without one. Last before the stop, so this refusal counts toward no lockout a step above meets.
            using var anonymous = new HttpClient(new SocketsHttpHandler { SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true } });
            var node = $"https://127.0.0.1:{_McpPort(run.Output)}";
            using var health = await anonymous.GetAsync($"{node}/healthz", timeout.Token);
            Assert.Equal(
                (HttpStatusCode.OK, """{"status":"healthy","sections":[{"name":"workflows-scheduler","healthy":true}]}"""),
                (health.StatusCode, await health.Content.ReadAsStringAsync(timeout.Token)));
            using var whoami = await anonymous.GetAsync($"{node}/api/v1/whoami", timeout.Token);
            Assert.Equal(HttpStatusCode.Unauthorized, whoami.StatusCode);

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

    // What the operator set up before: a desk, SDK and TTY profiles, the node door on `port`, encrypted credentials,
    // Discord at `webhookUrl`, a controller key and the echo plugin. Written by the backend's own stores, in-process;
    // returns the node's fingerprint and the controller key.
    internal static async Task<(string Fingerprint, string ControllerKey)> _PrepareStateRootAsync(string stateRoot, int port, string root, string webhookUrl)
    {
        var previous = Environment.GetEnvironmentVariable(CockpitBuild.StateRootVariable);
        Environment.SetEnvironmentVariable(CockpitBuild.StateRootVariable, stateRoot);

        // AC-1456: the key holder is process-wide; one left unlocked by an earlier preparation would encrypt with its key.
        SecretKeyHolder.Shared.Lock();
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
                new SessionProfile("EchoSignIn", new PluginProviderConfig("echo-provider.echo", new JsonObject { ["signedInFile"] = Path.Combine(root, SignedInFile) }.ToJsonString())) { DefaultKind = ProfileSessionKind.Sdk },
            ]);
            File.WriteAllText(Path.Combine(root, SignedInFile), "");
            await services.GetRequiredService<INotificationSettingsStore>().SaveAsync(new NotificationSettings { DiscordEnabled = true, WebhookUrl = webhookUrl, LoginCheckInterval = LoginCheckInterval });
            await services.GetRequiredService<INodeEndpointSettingsStore>().SaveAsync(new NodeEndpointSettings { Enabled = true, SharedSecret = Guid.NewGuid().ToString("N"), Port = port });

            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            await services.GetRequiredService<ISecretProtectionService>().EnableAsync(password);
            _Secret(root, "unlock", password);

            foreach (var (id, directory) in new[] { ("echo-provider", "EchoProviderDirectory"), ("workflows", "WorkflowsDirectory") })
            {
                var plugin = Directory.CreateDirectory(Path.Combine(root, "bundled", id)).FullName;
                foreach (var file in Directory.EnumerateFiles(_Metadata(directory)))
                {
                    File.Copy(file, Path.Combine(plugin, Path.GetFileName(file)));
                }
            }

            Assert.Equal(["echo-provider", "workflows"], (await new BundledPluginInstaller().InstallAsync(Path.Combine(root, "bundled"), PluginBootstrap.PluginsRoot)).Order());
            var issuer = new NodeCaller("journey", "", ConnectKeyCapability.Admin, "127.0.0.1", CancellationToken.None);
            var controllerKey = await services.GetRequiredService<ConnectKeyVerifier>().IssueAsync("controller", ConnectKeyCapability.Admin, 1, issuer, holdsAssistant: true);
            return (services.GetRequiredService<NodeSelfSignedCertificate>().Fingerprint, controllerKey.Secret);
        }
        finally
        {
            SecretKeyHolder.Shared.Lock();
            Environment.SetEnvironmentVariable(CockpitBuild.StateRootVariable, previous);
        }
    }

    // A controller as NodeSessionsClient opens one: the node's certificate pinned, the key as its bearer.
    private static async Task<McpClient> _ControllerAsync(int port, string key, string fingerprint)
    {
        var url = $"https://127.0.0.1:{port}/mcp";
        var server = new McpServerConfig { Name = "journey-controller", Transport = McpTransport.Http, Url = url, PinnedCertificateFingerprint = fingerprint };
        return await McpClient.CreateAsync(NodeCertificatePin.TransportFor(server, new HttpClientTransportOptions
        {
            Endpoint = new Uri(url),
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {key}" },
        }));
    }

    // What waits in the controller's inbox on the node, read from the start.
    private static async Task<List<(string Kind, string Body)>> _ReadNodeInboxAsync(McpClient controller)
    {
        var result = await controller.CallToolAsync("read_node_inbox", new Dictionary<string, object?>());
        var answer = JsonNode.Parse(string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text)));
        Assert.True(answer?["ok"]?.GetValue<bool>(), answer?.ToJsonString());
        return answer?["messages"]?.AsArray().Select(message => (message?["kind"]?.GetValue<string>() ?? "", message?["body"]?.GetValue<string>() ?? "")).ToList() ?? [];
    }

    // Discord as the server reaches it: a webhook on loopback that keeps every post.
    private static async Task<FakeDiscord> _FakeDiscordAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var discord = new FakeDiscord(app);
        app.MapPost("/webhook", async (HttpRequest request) =>
        {
            using var reader = new StreamReader(request.Body);
            discord.Add(JsonNode.Parse(await reader.ReadToEndAsync())?["content"]?.GetValue<string>() ?? "");
            return Results.NoContent();
        });
        await app.StartAsync();
        return discord;
    }

    internal static ServerRun _RunServer(string serverDirectory, string stateRoot, string unlockFile, string keyFile)
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

    internal static string _Secret(string root, string name, string value)
    {
        var path = Path.Combine(root, name);
        File.WriteAllText(path, value);
        return path;
    }

    internal static int _McpPort(string output)
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

    internal static string _Metadata(string key) =>
        typeof(ServerJourney).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(attribute => attribute.Key == key).Value
        ?? throw new InvalidOperationException($"The journeys were built without {key}.");

    private sealed class FakeDiscord(WebApplication app) : IAsyncDisposable
    {
        private readonly List<string> _posts = [];

        public string Url => $"{app.Urls.First()}/webhook";

        public IReadOnlyList<string> Posts
        {
            get
            {
                lock (_posts)
                {
                    return [.. _posts];
                }
            }
        }

        public void Add(string post)
        {
            lock (_posts)
            {
                _posts.Add(post);
            }
        }

        public int Count(string text) => Posts.Count(post => post.Contains(text, StringComparison.Ordinal));

        public async Task WaitForAsync(string text, int count, CancellationToken cancellationToken)
        {
            while (Count(text) < count)
            {
                await Task.Delay(100, cancellationToken);
            }
        }

        public ValueTask DisposeAsync() => app.DisposeAsync();
    }

    // The server's console, collected; `Running` completes with the line that says it is up.
    internal sealed class ServerRun
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

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.ViewTests;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Shell;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;

namespace Cockpit.Journeys;

[Collection(JourneyCollection.Alone)]
public sealed class LocalLlmJourney
{
    private const string ProfileName = JourneyHost.EchoProfile;
    private const string BranchName = "ac-1936-model-task";
    private const string CommitMessage = "AC-1936 mini task";
    private static readonly TimeSpan TurnTimeout = TimeSpan.FromMinutes(3);

    [SkippableFact]
    public async Task LmStudio_CompletesAMiniCodingTaskWithTheRealCockpitToolset()
    {
        var url = Environment.GetEnvironmentVariable("LOCAL_LLM_URL");
        var model = Environment.GetEnvironmentVariable("LOCAL_LLM_MODEL");
        Skip.If(string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(model), "LOCAL_LLM_URL and LOCAL_LLM_MODEL are required for the opt-in LM Studio journey.");

        var outputRoot = Path.Combine(AppContext.BaseDirectory, "TestResults", "AC-1936");
        var logPath = Path.Combine(outputRoot, "lm-studio-wire.log");
        Directory.CreateDirectory(outputRoot);
        var outerRepository = (await _GitAsync(Directory.GetCurrentDirectory(), "rev-parse", "--show-toplevel")).StandardOutput.Trim();
        var outerBranch = (await _GitAsync(outerRepository, "branch", "--show-current")).StandardOutput.Trim();
        var outerHead = (await _GitAsync(outerRepository, "rev-parse", "HEAD")).StandardOutput.Trim();
        var runRoot = Path.Combine(Path.GetTempPath(), "cockpit-ac-1936-" + Guid.NewGuid().ToString("N"));
        var wire = new LmStudioWireLog();
        wire.Note($"logPath={logPath}");

        try
        {
            var target = _V1Base(url!);
            await _VerifyModelAsync(target, model!, wire);

            await using var proxy = await LmStudioProxy.StartAsync(target, wire);
            var stateRoot = Path.Combine(outputRoot, "state");
            var productionRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cockpit");
            Assert.NotEqual(Path.GetFullPath(productionRoot), Path.GetFullPath(stateRoot));
            wire.Note($"stateRoot={stateRoot}");
            wire.Note($"productionStateRootExcluded={productionRoot}");

            var repository = Path.Combine(runRoot, "repo");
            Assert.False(Path.GetFullPath(repository).StartsWith(Path.GetFullPath(outerRepository), StringComparison.OrdinalIgnoreCase));
            await _CreateRepositoryAsync(repository);

            await using var cockpit = JourneyHost.DesktopWithProductionDrivers(stateRoot: stateRoot);
            await cockpit.StartDesktopAsync();
            cockpit.Services.GetRequiredService<IShellAccessSwitch>().Enabled = true;
            await cockpit.Services.GetRequiredService<ISessionProfileStore>().SaveAsync(
            [
                new SessionProfile(
                    ProfileName,
                    new LmStudioConfig(proxy.BaseUrl, model!),
                    Defaults: new ProfileDefaults(string.Empty, string.Empty, string.Empty, AutoApproveTools: true))
                {
                    EnabledMcpServerNames = ["cockpit-coding-shell"],
                },
            ]);
            var project = await cockpit.Services.GetRequiredService<IProjectEditor>().AddNewProjectAsync(
                Project.Create("AC-1936 mini task") with { SourceDirectories = [new ProjectRepository(repository)] });
            var session = await cockpit.StartSessionThroughTheDialogAsync(project);

            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnTurnEnded(SessionTurnEnd _) => ended.TrySetResult();
            session.TurnEnded += OnTurnEnded;
            try
            {
                await HeadlessAvalonia.RunAsync(async () =>
                {
                    session.InputText = $"Create branch {BranchName}, replace the only line 'before' in task.txt with 'after', then commit with message '{CommitMessage}'. Use the available tools and do not merely describe the work.";
                    await session.SendCommand.ExecuteAsync(null);
                });
                await ended.Task.WaitAsync(TurnTimeout);
            }
            finally
            {
                session.TurnEnded -= OnTurnEnded;
            }

            foreach (var tool in HeadlessAvalonia.Run(() => session.Transcript.Where(entry => entry.IsToolUse).ToList()))
            {
                wire.Tool(tool.ToolName ?? string.Empty, tool.InputJson ?? string.Empty, tool.ResultText, tool.IsResultError);
            }

            var branch = await _GitAsync(repository, "show-ref", "--verify", "--quiet", $"refs/heads/{BranchName}");
            wire.Note($"commitBranchExitCode={branch.ExitCode}");
            Assert.Contains(wire.ToolNames, name => name.Contains("shell", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(0, branch.ExitCode);
            var commit = await _GitAsync(repository, "log", "-1", "--format=%s", BranchName);
            Assert.Equal(CommitMessage, commit.StandardOutput.Trim());
            var content = await _GitAsync(repository, "show", $"{BranchName}:task.txt");
            Assert.Equal("after", content.StandardOutput.Trim());
            Assert.Equal(outerBranch, (await _GitAsync(outerRepository, "branch", "--show-current")).StandardOutput.Trim());
            Assert.Equal(outerHead, (await _GitAsync(outerRepository, "rev-parse", "HEAD")).StandardOutput.Trim());
        }
        finally
        {
            if (Directory.Exists(runRoot))
            {
                await _DeleteRunRootAsync(runRoot);
            }
            await File.WriteAllTextAsync(logPath, wire.Render());
            Console.WriteLine($"LM Studio wire log: {logPath}");
        }
    }

    private static Uri _V1Base(string value)
    {
        var baseUrl = value.TrimEnd('/');
        return new Uri(baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? baseUrl + "/" : baseUrl + "/v1/");
    }

    private static async Task _VerifyModelAsync(Uri target, string model, LmStudioWireLog wire)
    {
        using var client = new HttpClient();
        using var response = await client.GetAsync(new Uri(target, "models"));
        var body = await response.Content.ReadAsStringAsync();
        wire.Http("preflight-request", "GET", new Uri(target, "models").ToString(), string.Empty);
        wire.Http("preflight-response", ((int)response.StatusCode).ToString(), response.ReasonPhrase ?? string.Empty, body);
        response.EnsureSuccessStatusCode();
        var models = JsonNode.Parse(body)?["data"]?.AsArray().Select(item => item?["id"]?.GetValue<string>());
        Assert.Contains(model, models ?? []);
    }

    private static async Task _CreateRepositoryAsync(string repository)
    {
        Directory.CreateDirectory(repository);
        await _GitAsync(repository, "init", "-b", "main");
        await _GitAsync(repository, "config", "user.email", "ac-1936@journey.invalid");
        await _GitAsync(repository, "config", "user.name", "AC-1936 Journey");
        await File.WriteAllTextAsync(Path.Combine(repository, "task.txt"), "before\n");
        Assert.Equal(0, (await _GitAsync(repository, "add", "task.txt")).ExitCode);
        Assert.Equal(0, (await _GitAsync(repository, "commit", "-m", "initial")).ExitCode);
    }

    private static async Task _DeleteRunRootAsync(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(entry, FileAttributes.Normal);
                }
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (UnauthorizedAccessException) when (attempt < 19)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250));
            }
        }
    }

    private static async Task<GitResult> _GitAsync(string directory, params string[] arguments)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new GitResult(process.ExitCode, await output, await error);
    }

    private sealed record GitResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed class LmStudioWireLog
    {
        private long _sequence;
        private readonly ConcurrentQueue<string> _entries = [];
        private readonly ConcurrentQueue<string> _toolNames = [];

        public IReadOnlyCollection<string> ToolNames => _toolNames;

        public void Note(string text) => _Add("note", text);

        public void Http(string kind, string method, string target, string body) => _Add(kind, $"{method} {target}\n{body}");

        public void Tool(string name, string input, string? result, bool isError)
        {
            _toolNames.Enqueue(name);
            _Add("tool-call", $"{name}\ninput={input}");
            _Add("tool-result", $"error={isError}\n{result}");
        }

        public string Render() => string.Join(Environment.NewLine, _entries);

        private void _Add(string kind, string text) => _entries.Enqueue($"[{Interlocked.Increment(ref _sequence):D4}] {kind}\n{_Redact(text)}\n");

        private static string _Redact(string value) => System.Text.RegularExpressions.Regex.Replace(value, "(?im)^(Authorization:\\s*).+$", "$1***");
    }

    private sealed class LmStudioProxy : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly HttpClient _client = new();
        private readonly Uri _target;
        private readonly LmStudioWireLog _wire;
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _serve;

        private LmStudioProxy(HttpListener listener, Uri target, LmStudioWireLog wire)
        {
            _listener = listener;
            _target = target;
            _wire = wire;
            _serve = Task.Run(_ServeAsync);
        }

        public string BaseUrl => _listener.Prefixes.Single().TrimEnd('/');

        public static Task<LmStudioProxy> StartAsync(Uri target, LmStudioWireLog wire)
        {
            int port;
            using (var portProbe = new TcpListener(IPAddress.Loopback, 0))
            {
                portProbe.Start();
                port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
            }

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            return Task.FromResult(new LmStudioProxy(listener, target, wire));
        }

        public async ValueTask DisposeAsync()
        {
            _stopping.Cancel();
            _listener.Close();
            try
            {
                await _serve;
            }
            catch (OperationCanceledException)
            {
            }

            _client.Dispose();
            _stopping.Dispose();
        }

        private async Task _ServeAsync()
        {
            while (!_stopping.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().WaitAsync(_stopping.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (HttpListenerException) when (_stopping.IsCancellationRequested)
                {
                    break;
                }

                await _ForwardAsync(context);
            }
        }

        private async Task _ForwardAsync(HttpListenerContext context)
        {
            var body = await new StreamReader(context.Request.InputStream, context.Request.ContentEncoding).ReadToEndAsync(_stopping.Token);
            _wire.Http("request", context.Request.HttpMethod, context.Request.RawUrl ?? string.Empty, body);
            var relative = (context.Request.RawUrl ?? string.Empty).TrimStart('/');
            if (_target.AbsolutePath.TrimEnd('/').EndsWith("/v1", StringComparison.OrdinalIgnoreCase) && relative.StartsWith("v1/", StringComparison.OrdinalIgnoreCase))
            {
                relative = relative[3..];
            }

            using var request = new HttpRequestMessage(new HttpMethod(context.Request.HttpMethod), new Uri(_target, relative));
            if (context.Request.HasEntityBody)
            {
                request.Content = new StringContent(body, context.Request.ContentEncoding);
                if (context.Request.ContentType is { Length: > 0 })
                {
                    request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(context.Request.ContentType);
                }
            }

            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _stopping.Token);
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
            await using var source = await response.Content.ReadAsStreamAsync(_stopping.Token);
            await using var capture = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await source.ReadAsync(buffer, _stopping.Token)) > 0)
            {
                await context.Response.OutputStream.WriteAsync(buffer.AsMemory(0, read), _stopping.Token);
                await context.Response.OutputStream.FlushAsync(_stopping.Token);
                await capture.WriteAsync(buffer.AsMemory(0, read), _stopping.Token);
            }

            _wire.Http("response", ((int)response.StatusCode).ToString(), context.Request.RawUrl ?? string.Empty, Encoding.UTF8.GetString(capture.ToArray()));
            context.Response.Close();
        }
    }
}

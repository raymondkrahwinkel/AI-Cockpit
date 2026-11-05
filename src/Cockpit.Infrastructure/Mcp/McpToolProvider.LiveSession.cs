using System.Threading.Channels;
using Cockpit.Core.Mcp;
using Cockpit.Core.Sessions.Permissions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Cockpit.Infrastructure.Mcp;

internal sealed partial class McpToolProvider
{
    private sealed class LiveMcpToolSession : IMcpToolSession
    {
        private readonly McpToolProvider _provider;
        private readonly string? _paneId;
        private readonly string? _projectId;
        private readonly string? _agentName;
        private readonly bool _ownsToken;
        private readonly Lock _sync = new();
        private readonly Dictionary<string, Upstream> _servers = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<Upstream> _retired = [];
        private bool _disposed;
        private readonly HashSet<(string Server, string Instructions)> _deliveredInstructions = [];

        public LiveMcpToolSession(McpToolProvider provider, string? paneId, string? token, string? projectId, string? agentName, bool ownsToken)
        {
            _provider = provider;
            _paneId = paneId;
            PaneToken = token;
            _projectId = projectId;
            _agentName = agentName;
            _ownsToken = ownsToken;
            _provider._oauthCoordinator.AccountChanged += _AccountChanged;
        }

        public event Action? ToolsChanged;
        public string? PaneToken { get; }

        public IReadOnlyList<McpSessionTool> Tools
        {
            get
            {
                lock (_sync)
                {
                    return [.. _servers.Values.SelectMany(server => server.Connection?.Tools.Select(tool => new McpSessionTool(tool, server.Config.Name, server.Config.AlwaysMounted)) ?? [])];
                }
            }
        }

        public IReadOnlyList<McpServerTool> ForwardTools
        {
            get
            {
                lock (_sync)
                {
                    return [.. _servers.Values.SelectMany(server => server.Connection?.Tools.OfType<McpClientTool>().Select(tool => new ForwardMcpServerTool(
                        server.Config.Name, tool.ProtocolTool, (context, ct) => _CallAsync(server, tool.Name, context, ct))) ?? [])];
                }
            }
        }

        public IReadOnlyList<string> ConnectedServerNames
        {
            get
            {
                lock (_sync)
                {
                    return [.. _servers.Values.Where(server => server.Connection is not null).Select(server => server.Config.Name)];
                }
            }
        }

        public IReadOnlyList<string> ServersNeedingSignIn => [.. ConnectionIssues.Where(issue => issue.Reason == NeedsSignIn).Select(issue => issue.Name)];

        public IReadOnlyList<McpServerConnectionIssue> ConnectionIssues
        {
            get
            {
                lock (_sync)
                {
                    return [.. _servers.Values.Where(server => server.Issue is not null).Select(server => new McpServerConnectionIssue(server.Config.Name, server.Issue ?? "Unavailable"))];
                }
            }
        }

        public IReadOnlyDictionary<string, ToolPermissionClass> ToolClasses
        {
            get
            {
                lock (_sync)
                {
                    var classes = new Dictionary<string, ToolPermissionClass>(StringComparer.Ordinal);
                    foreach (var pair in _servers.Values.SelectMany(server => server.Connection?.ToolClasses ?? new Dictionary<string, ToolPermissionClass>()))
                    {
                        classes[pair.Key] = classes.TryGetValue(pair.Key, out var previous) ? DelegatedToolPermissionPolicy.MoreRestrictive(previous, pair.Value) : pair.Value;
                    }
                    return classes;
                }
            }
        }

        public string? ServerInstructions
        {
            get
            {
                lock (_sync)
                {
                    var instructions = new List<string>();
                    foreach (var server in _servers.Values)
                    {
                        if (server.Connection?.Client.ServerInstructions is { Length: > 0 } text)
                        {
                            _deliveredInstructions.Add((server.Config.Name, text));
                            instructions.Add($"## {server.Config.Name}\n{text}");
                        }
                    }
                    return string.Join("\n\n", instructions);
                }
            }
        }

        public Task AddServerAsync(McpServerConfig server, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_servers.TryGetValue(server.Name, out var existing))
                {
                    return existing.Initial.Task.WaitAsync(cancellationToken);
                }
                var upstream = new Upstream(server);
                _servers.Add(server.Name, upstream);
                upstream.Worker = Task.Run(() => _RunAsync(upstream));
                return upstream.Initial.Task.WaitAsync(cancellationToken);
            }
        }

        public void RemoveServer(string serverName)
        {
            lock (_sync)
            {
                if (!_servers.Remove(serverName, out var server))
                {
                    return;
                }
                _retired.Add(server);
                server.Lifetime.Cancel();
            }
            _Changed();
        }

        private void _AccountChanged(McpServerConfig config)
        {
            lock (_sync)
            {
                if (_servers.TryGetValue(config.Name, out var server))
                {
                    server.Signals.Writer.TryWrite(true);
                }
            }
        }

        private async Task _RunAsync(Upstream server)
        {
            var ct = server.Lifetime.Token;
            var failures = 0;
            IAsyncDisposable? notification = null;
            McpClient? client = null;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var config = server.Config;
                    if (!_ownsToken)
                    {
                        var registry = await _provider._catalog.GetServersForSessionAsync(_projectId, new HashSet<string>([config.Name], StringComparer.OrdinalIgnoreCase), ct);
                        config = registry.FirstOrDefault(candidate => candidate.Name.Equals(config.Name, StringComparison.OrdinalIgnoreCase)) ?? config with { Enabled = false };
                    }
                    var outcome = config.Enabled
                        ? await _provider._ConnectServerAsync(config, PaneToken, ct, _agentName)
                        : (Connection: (ServerConnection?)null, FailureReason: "Server disabled.");
                    client = outcome.Connection?.Client;
                    lock (_sync)
                    {
                        server.Config = config;
                        server.Issue = outcome.FailureReason;
                        if (outcome.Connection is not null || ++failures >= 3)
                        {
                            server.Connection = outcome.Connection;
                        }
                    }
                    server.Initial.TrySetResult();
                    _Changed();
                    if (client is null)
                    {
                        await _RetryDelayAsync(server, failures, ct);
                        continue;
                    }
                    failures = 0;
                    notification = client.RegisterNotificationHandler("notifications/tools/list_changed", (_, _) =>
                    {
                        server.Signals.Writer.TryWrite(false);
                        return ValueTask.CompletedTask;
                    });
                    var reconnect = false;
                    while (!reconnect)
                    {
                        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        var signal = server.Signals.Reader.WaitToReadAsync(waiting.Token).AsTask();
                        await Task.WhenAny(signal, client.Completion);
                        await waiting.CancelAsync();
                        ct.ThrowIfCancellationRequested();
                        reconnect = client.Completion.IsCompleted || (server.Signals.Reader.TryRead(out var reset) && reset);
                        if (reconnect)
                        {
                            break;
                        }
                        try
                        {
                            var tools = await client.ListToolsAsync(cancellationToken: ct);
                            lock (_sync)
                            {
                                server.Connection = new ServerConnection(client, [.. tools], config.Name,
                                    tools.ToDictionary(tool => tool.Name, tool => DelegatedToolPermissionPolicy.Classify(tool.ProtocolTool.Annotations?.ReadOnlyHint, tool.ProtocolTool.Annotations?.DestructiveHint)));
                            }
                            _Changed();
                        }
                        catch (Exception) when (!ct.IsCancellationRequested)
                        {
                            reconnect = true;
                        }
                    }
                    await notification.DisposeAsync();
                    notification = null;
                    await _DisposeClientAsync(client);
                    client = null;
                    await _RetryDelayAsync(server, 1, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception)
            {
                lock (_sync)
                {
                    server.Connection = null;
                    server.Issue = "Upstream connection unavailable.";
                }
                _Changed();
            }
            finally
            {
                server.Initial.TrySetResult();
                if (notification is not null)
                {
                    await notification.DisposeAsync();
                }
                if (client is not null)
                {
                    await _DisposeClientAsync(client);
                }
            }
        }

        private static async Task _DisposeClientAsync(McpClient client)
        {
            try
            {
                await client.DisposeAsync();
            }
            catch (Exception)
            {
                // Teardown of a dead upstream must not interrupt sibling connections.
            }
        }

        private static async Task _RetryDelayAsync(Upstream server, int failures, CancellationToken ct)
        {
            var seconds = failures switch { <= 1 => 1, 2 => 5, 3 => 30, _ => 300 };
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delay = Task.Delay(TimeSpan.FromSeconds(seconds), wait.Token);
            var signal = server.Signals.Reader.WaitToReadAsync(wait.Token).AsTask();
            await Task.WhenAny(delay, signal);
            await wait.CancelAsync();
            while (server.Signals.Reader.TryRead(out _)) { }
            ct.ThrowIfCancellationRequested();
        }

        private async ValueTask<CallToolResult> _CallAsync(Upstream server, string name, RequestContext<CallToolRequestParams> context, CancellationToken ct)
        {
            ServerConnection? connection;
            lock (_sync)
            {
                connection = _servers.ContainsValue(server) ? server.Connection : null;
            }
            if (connection is null || (!_ownsToken && _paneId is not null && _provider._mounts?.IsMounted(_paneId, server.Config.Name) != true))
            {
                return _Error(server.Config.Name, server.Issue ?? "Unavailable");
            }
            try
            {
                if (server.Config.Auth == McpServerAuth.OAuth
                    && (await _provider._oauthCoordinator.AcquireAsync(server.Config, interactive: false, ct)).State != McpAuthState.Authorized)
                {
                    server.Signals.Writer.TryWrite(true);
                    return _Error(server.Config.Name, NeedsSignIn);
                }
                var arguments = context.Params.Arguments?.ToDictionary(pair => pair.Key, pair => (object?)pair.Value);
                var progress = new ForwardProgress();
                var forwarding = _ForwardProgressAsync(progress, context, ct);
                CallToolResult result;
                try
                {
                    result = await connection.Client.CallToolAsync(name, arguments, progress, cancellationToken: ct);
                }
                finally
                {
                    progress.Values.Writer.TryComplete();
                    await forwarding;
                }
                lock (_sync)
                {
                    if (connection.Client.ServerInstructions is { Length: > 0 } text && _deliveredInstructions.Add((server.Config.Name, text)))
                    {
                        result.Content = [.. result.Content, new TextContentBlock { Text = $"## {server.Config.Name}\n{text}" }];
                    }
                }
                return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                server.Signals.Writer.TryWrite(true);
                return _Error(server.Config.Name, "Upstream call failed; reconnecting. The call was not retried.");
            }
        }

        private static async Task _ForwardProgressAsync(ForwardProgress progress, RequestContext<CallToolRequestParams> context, CancellationToken ct)
        {
            await foreach (var value in progress.Values.Reader.ReadAllAsync(ct))
            {
                if (context.Params.ProgressToken is { } token)
                {
                    await context.Server.NotifyProgressAsync(token, value, cancellationToken: ct);
                }
            }
        }

        private sealed class ForwardProgress : IProgress<ProgressNotificationValue>
        {
            public Channel<ProgressNotificationValue> Values { get; } = Channel.CreateUnbounded<ProgressNotificationValue>();
            public void Report(ProgressNotificationValue value) => Values.Writer.TryWrite(value);
        }

        private static CallToolResult _Error(string server, string reason) => new()
        {
            IsError = true,
            Content = [new TextContentBlock { Text = $"{server}: {reason}" }],
        };

        private void _Changed()
        {
            ToolsChanged?.Invoke();
            if (_paneId is not null)
            {
                _provider._mounts?.ReportConnections(_paneId, ConnectedServerNames, ConnectionIssues);
            }
        }

        public async ValueTask DisposeAsync()
        {
            Upstream[] servers;
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                servers = [.. _servers.Values, .. _retired];
                _servers.Clear();
            }
            _provider._oauthCoordinator.AccountChanged -= _AccountChanged;
            foreach (var server in servers)
            {
                await server.Lifetime.CancelAsync();
            }
            await Task.WhenAll(servers.Select(server => server.Worker));
            foreach (var server in servers)
            {
                server.Lifetime.Dispose();
            }
            if (_ownsToken && _paneId is not null && PaneToken is not null)
            {
                _provider._keyring.Revoke(_paneId, PaneToken);
            }
        }

        private sealed class Upstream(McpServerConfig config)
        {
            public McpServerConfig Config { get; set; } = config;
            public ServerConnection? Connection { get; set; }
            public string? Issue { get; set; }
            public CancellationTokenSource Lifetime { get; } = new();
            public Channel<bool> Signals { get; } = Channel.CreateUnbounded<bool>();
            public TaskCompletionSource Initial { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public Task Worker { get; set; } = Task.CompletedTask;
        }
    }
}

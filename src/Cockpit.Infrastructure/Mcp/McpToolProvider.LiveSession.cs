using System.Threading.Channels;
using Cockpit.Core.Mcp;
using Cockpit.Core.Sessions.Permissions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.AI;
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
        private readonly McpBackgroundTasks _retirements;
        private readonly HashSet<(string Server, string Instructions)> _deliveredInstructions = [];
        private bool _disposed;
        private int _changeDeferrals;
        private bool _changePending;
        private readonly Channel<bool> _mountChanges = Channel.CreateUnbounded<bool>();
        private readonly CancellationTokenSource _mountLifetime = new();
        private Task _mountWorker = Task.CompletedTask;
        private readonly McpToolNames _toolNames;

        public LiveMcpToolSession(McpToolProvider provider, string? paneId, string? token, string? projectId, string? agentName, bool ownsToken)
        {
            _provider = provider;
            _toolNames = paneId is null ? new() : provider._mounts?.Set(paneId)?.ToolNames ?? new();
            _paneId = paneId;
            PaneToken = token;
            _projectId = projectId;
            _agentName = agentName;
            _ownsToken = ownsToken;
            _retirements = new(provider._logger);
            if (!ownsToken)
            {
                _provider._oauthCoordinator.AccountChanged += _AccountChanged;
            }
        }

        public event Action? ToolsChanged;
        public string? PaneToken { get; }

        public void WatchMounts(string? confineRoot)
        {
            if (_paneId is null || _provider._mounts is null)
            {
                return;
            }
            _provider._mounts.Changed += _MountChanged;
            _mountWorker = _WatchMountsAsync(confineRoot);
        }

        private void _MountChanged(string paneId)
        {
            if (paneId == _paneId)
            {
                _mountChanges.Writer.TryWrite(true);
            }
        }

        private async Task _WatchMountsAsync(string? confineRoot)
        {
            var ct = _mountLifetime.Token;
            try
            {
                while (await _mountChanges.Reader.WaitToReadAsync(ct))
                {
                    while (_mountChanges.Reader.TryRead(out _)) { }
                    try
                    {
                        var set = _paneId is null ? null : _provider._mounts?.Set(_paneId);
                        if (set is null) { continue; }
                        var registry = await _provider._catalog.GetServersForSessionAsync(_projectId, set.ServerNames, ct);
                        var servers = registry.Where(server => server.Enabled && set.ServerNames.Contains(server.Name)).ToList();
                        await UpdateServersAsync(confineRoot is null ? servers : _ConfinedServers(servers, confineRoot));
                    }
                    catch (Exception) when (!ct.IsCancellationRequested)
                    {
                        _provider._logger.LogWarning("Local MCP set refresh failed.");
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
        }

        public IReadOnlyList<McpSessionTool> Tools
        {
            get
            {
                lock (_sync)
                {
                    return [.. _servers.Values.SelectMany(server => server.Tools.Select(tool => new McpSessionTool(new MountedTool(tool, this, server), server.Config.Name, server.Config.AlwaysMounted)))];
                }
            }
        }

        public IReadOnlyList<McpServerTool> ForwardTools
        {
            get
            {
                lock (_sync)
                {
                    var tools = new List<McpServerTool>();
                    foreach (var server in _servers.Values.OrderBy(server => server.Config.Name, StringComparer.Ordinal))
                    {
                        foreach (var tool in server.Tools.OfType<McpClientTool>())
                        {
                            var name = _toolNames.ForTool(server.Config.Name, tool.Name);
                            tools.Add(new ForwardMcpServerTool(name, tool.ProtocolTool, server, (context, ct) => _CallAsync(server, tool.Name, context, ct)));
                        }
                    }
                    return tools;
                }
            }
        }

        public IReadOnlyList<McpSessionTool> GatewayTools
        {
            get
            {
                lock (_sync)
                {
                    return [.. _servers.Values.SelectMany(server => server.Tools.Select(tool => new McpSessionTool(
                        new MountedTool(tool, this, server, _toolNames.ForTool(server.Config.Name, tool.Name)), server.Config.Name, server.Config.AlwaysMounted)))];
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

        public string CaptureServerInstructions()
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

        public async Task WaitForConnectionsAsync(CancellationToken cancellationToken)
        {
            Task[] connecting;
            lock (_sync)
            {
                connecting = [.. _servers.Values.Where(server => server.Connecting is not null).Select(server => server.Connecting?.Task ?? Task.CompletedTask)];
            }
            await Task.WhenAll(connecting).WaitAsync(cancellationToken);
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
                upstream.Worker = Task.Run(() => _ownsToken ? _ConnectLocalAsync(upstream) : _RunGatewayAsync(upstream));
                return upstream.Initial.Task.WaitAsync(cancellationToken);
            }
        }

        public void RemoveServer(string serverName)
        {
            lock (_sync)
            {
                if (_disposed || !_servers.Remove(serverName, out var server))
                {
                    return;
                }
                _retirements.Run(async () =>
                {
                    await server.Drained.Task;
                    await server.Lifetime.CancelAsync();
                    await server.Worker;
                    await _DisposeClientAsync(server.Connection?.Client);
                    server.Lifetime.Dispose();
                });
            }
            _Changed();
        }

        public async Task UpdateServersAsync(IReadOnlyList<McpServerConfig> servers)
        {
            Task connecting;
            lock (_sync)
            {
                _changeDeferrals++;
                foreach (var name in _servers.Keys.Except(servers.Select(server => server.Name), StringComparer.OrdinalIgnoreCase).ToArray())
                {
                    RemoveServer(name);
                }
                connecting = Task.WhenAll(servers.Select(server => AddServerAsync(server)));
            }
            try
            {
                await connecting;
            }
            finally
            {
                bool publish;
                lock (_sync)
                {
                    publish = --_changeDeferrals == 0 && _changePending;
                    _changePending = false;
                }
                if (publish)
                {
                    _Changed();
                }
            }
        }

        private async Task _ConnectLocalAsync(Upstream server)
        {
            try
            {
                var outcome = await _provider._ConnectServerAsync(server.Config, PaneToken, server.Lifetime.Token, _agentName);
                _Publish(server, outcome.Connection, outcome.FailureReason);
            }
            finally
            {
                _FinishConnecting(server);
            }
        }

        private void _AccountChanged(McpServerConfig config)
        {
            lock (_sync)
            {
                if (_servers.TryGetValue(config.Name, out var server))
                {
                    server.AccountChanges.Writer.TryWrite(true);
                    server.Signals.Writer.TryWrite(true);
                }
            }
        }

        private async Task _RunGatewayAsync(Upstream server)
        {
            var ct = server.Lifetime.Token;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (server.Failures > 0)
                    {
                        await _RetryDelayAsync(server, ct);
                    }
                    while (server.Signals.Reader.TryRead(out _)) { }
                    McpClient? client = null;
                    IAsyncDisposable? notification = null;
                    lock (_sync)
                    {
                        server.Connecting = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                    try
                    {
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        deadline.CancelAfter(TimeSpan.FromSeconds(15));
                        var registry = await _provider._catalog.GetServersForSessionAsync(_projectId, new HashSet<string>([server.Config.Name], StringComparer.OrdinalIgnoreCase), deadline.Token);
                        var config = registry.FirstOrDefault(candidate => candidate.Name.Equals(server.Config.Name, StringComparison.OrdinalIgnoreCase)) ?? server.Config with { Enabled = false };
                        var outcome = config.Enabled
                            ? await _provider._ConnectServerAsync(config, PaneToken, deadline.Token, _agentName, gateway: true)
                            : (Connection: (ServerConnection?)null, FailureReason: "Server disabled.");
                        client = outcome.Connection?.Client;
                        lock (_sync)
                        {
                            server.Config = config;
                        }
                        if (client is null)
                        {
                            _Failed(server, outcome.FailureReason ?? "Upstream unavailable.");
                            continue;
                        }
                        _Publish(server, outcome.Connection, null);
                        _FinishConnecting(server);
                        notification = client.RegisterNotificationHandler("notifications/tools/list_changed", (_, _) =>
                        {
                            server.Signals.Writer.TryWrite(false);
                            return ValueTask.CompletedTask;
                        });
                        await _MonitorAsync(server, client, ct);
                        _Failed(server, server.Issue ?? "Upstream disconnected.");
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        _provider._logger.LogWarning("MCP catalog or connection refresh failed for {Server}; retrying.", server.Config.Name);
                        _Failed(server, "Upstream connection unavailable.");
                    }
                    finally
                    {
                        _FinishConnecting(server);
                        if (notification is not null)
                        {
                            await notification.DisposeAsync();
                        }
                        await _DisposeClientAsync(client);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            finally
            {
                _FinishConnecting(server);
            }
        }

        private async Task _MonitorAsync(Upstream server, McpClient client, CancellationToken ct)
        {
            while (true)
            {
                using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var signal = server.Signals.Reader.WaitToReadAsync(waiting.Token).AsTask();
                await Task.WhenAny(signal, client.Completion);
                await waiting.CancelAsync();
                ct.ThrowIfCancellationRequested();
                if (client.Completion.IsCompleted || (server.Signals.Reader.TryRead(out var reset) && reset))
                {
                    return;
                }
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    deadline.CancelAfter(TimeSpan.FromSeconds(15));
                    var tools = await client.ListToolsAsync(cancellationToken: deadline.Token);
                    _Publish(server, new ServerConnection(client, [.. tools], server.Config.Name,
                        tools.ToDictionary(tool => tool.Name, tool => DelegatedToolPermissionPolicy.Classify(tool.ProtocolTool.Annotations?.ReadOnlyHint, tool.ProtocolTool.Annotations?.DestructiveHint))), null);
                }
                catch (Exception exception) when (!ct.IsCancellationRequested)
                {
                    _provider._logger.LogWarning("MCP tools refresh failed for {Server}.", server.Config.Name);
                    if (_TransportFailed(exception, client))
                    {
                        return;
                    }
                }
            }
        }

        private void _Publish(Upstream server, ServerConnection? connection, string? issue)
        {
            lock (_sync)
            {
                server.Connection = connection;
                server.Tools = connection?.Tools ?? [];
                server.Issue = issue;
            }
            _Changed();
        }

        private void _Failed(Upstream server, string issue)
        {
            lock (_sync)
            {
                server.Connection = null;
                server.Issue = issue;
                if (++server.Failures >= 3)
                {
                    server.Tools = [];
                }
            }
            _Changed();
        }

        private void _FinishConnecting(Upstream server)
        {
            lock (_sync)
            {
                server.Connecting?.TrySetResult();
                server.Connecting = null;
                server.Initial.TrySetResult();
            }
        }

        private static async Task _DisposeClientAsync(McpClient? client)
        {
            if (client is null)
            {
                return;
            }
            try
            {
                await client.DisposeAsync();
            }
            catch (Exception)
            {
                // Teardown of a dead upstream must not interrupt sibling connections.
            }
        }

        private static async Task _RetryDelayAsync(Upstream server, CancellationToken ct)
        {
            var seconds = server.Failures switch { <= 1 => 1, 2 => 5, 3 => 30, _ => 300 };
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delay = Task.Delay(TimeSpan.FromSeconds(seconds), wait.Token);
            var account = server.AccountChanges.Reader.WaitToReadAsync(wait.Token).AsTask();
            await Task.WhenAny(delay, account);
            await wait.CancelAsync();
            while (server.AccountChanges.Reader.TryRead(out _)) { }
            ct.ThrowIfCancellationRequested();
        }

        private async ValueTask<CallToolResult> _CallAsync(Upstream server, string name, RequestContext<CallToolRequestParams> context, CancellationToken ct)
        {
            if (!_TryAcquire(server))
            {
                return _Error(server.Config.Name, "Server removed from this session's MCP set.");
            }
            try
            {
                return await _CallUpstreamAsync(server, name, context, ct);
            }
            finally
            {
                _Release(server);
            }
        }

        private bool _TryAcquire(Upstream server)
        {
            lock (_sync)
            {
                if (_disposed || !_servers.ContainsValue(server)
                    || (_paneId is not null && _provider._mounts?.IsMounted(_paneId, server.Config.Name) == false))
                {
                    return false;
                }
                if (server.ActiveCalls++ == 0)
                {
                    server.Drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                return true;
            }
        }

        private void _Release(Upstream server)
        {
            lock (_sync)
            {
                if (--server.ActiveCalls == 0)
                {
                    server.Drained.TrySetResult();
                }
            }
        }

        private async ValueTask<CallToolResult> _CallUpstreamAsync(Upstream server, string name, RequestContext<CallToolRequestParams> context, CancellationToken ct)
        {
            ServerConnection? connection;
            lock (_sync)
            {
                connection = server.Connection;
            }
            if (connection is null || connection.Client.Completion.IsCompleted)
            {
                return _Error(server.Config.Name, server.Issue ?? "Unavailable");
            }
            try
            {
                if (server.Config.Auth == McpServerAuth.OAuth
                    && (await _provider._oauthCoordinator.AcquireAsync(server.Config, interactive: false, ct)).State != McpAuthState.Authorized)
                {
                    lock (_sync)
                    {
                        server.Connection = null;
                        server.Issue = NeedsSignIn;
                    }
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
                    if (ReferenceEquals(server.Connection, connection))
                    {
                        server.Failures = 0;
                    }
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
            catch (Exception exception)
            {
                if (_TransportFailed(exception, connection.Client))
                {
                    var reconnect = false;
                    lock (_sync)
                    {
                        if (ReferenceEquals(server.Connection, connection))
                        {
                            server.Connection = null;
                            server.Issue = "Upstream call failed; reconnecting. The call was not retried.";
                            reconnect = true;
                        }
                    }
                    if (reconnect)
                    {
                        server.Signals.Writer.TryWrite(true);
                    }
                    return _Error(server.Config.Name, "Upstream call failed; reconnecting. The call was not retried.");
                }
                return _Error(server.Config.Name, "Upstream rejected the tool request.");
            }
        }

        private static bool _TransportFailed(Exception exception, McpClient client) =>
            client.Completion.IsCompleted || exception is HttpRequestException or IOException;

        private async Task _ForwardProgressAsync(ForwardProgress progress, RequestContext<CallToolRequestParams> context, CancellationToken ct)
        {
            try
            {
                await foreach (var value in progress.Values.Reader.ReadAllAsync(ct))
                {
                    if (context.Params.ProgressToken is { } token)
                    {
                        try
                        {
                            await context.Server.NotifyProgressAsync(token, value, cancellationToken: ct);
                        }
                        catch (Exception)
                        {
                            _provider._logger.LogDebug("MCP caller no longer receives progress.");
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
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
            lock (_sync)
            {
                if (_changeDeferrals > 0)
                {
                    _changePending = true;
                    return;
                }
            }
            foreach (var handler in ToolsChanged?.GetInvocationList() ?? [])
            {
                try
                {
                    ((Action)handler)();
                }
                catch (Exception)
                {
                    _provider._logger.LogWarning("MCP tools-changed handler failed.");
                }
            }
            if (_paneId is not null)
            {
                _provider._mounts?.ReportConnections(_paneId, ConnectedServerNames, ConnectionIssues,
                    _ => _provider._logger.LogWarning("MCP connection-report handler failed."));
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_provider._mounts is not null)
            {
                _provider._mounts.Changed -= _MountChanged;
            }
            await _mountLifetime.CancelAsync();
            Upstream[] servers;
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                servers = [.. _servers.Values];
                _servers.Clear();
            }
            _provider._oauthCoordinator.AccountChanged -= _AccountChanged;
            foreach (var server in servers)
            {
                await server.Lifetime.CancelAsync();
            }
            await Task.WhenAll(servers.Select(server => server.Worker));
            await _mountWorker;
            _mountLifetime.Dispose();
            foreach (var server in servers)
            {
                await _DisposeClientAsync(server.Connection?.Client);
                server.Lifetime.Dispose();
            }
            await _retirements.DrainAsync();
            if (_ownsToken && _paneId is not null && PaneToken is not null)
            {
                _provider._keyring.Revoke(_paneId, PaneToken);
            }
        }

        private sealed class MountedTool(AIFunction inner, LiveMcpToolSession session, Upstream server, string? name = null) : DelegatingAIFunction(inner)
        {
            public override string Name => name ?? base.Name;

            protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
            {
                if (!session._TryAcquire(server))
                {
                    return $"{server.Config.Name}: Server removed from this session's MCP set.";
                }
                try
                {
                    return await base.InvokeCoreAsync(arguments, cancellationToken);
                }
                finally
                {
                    session._Release(server);
                }
            }
        }

        private sealed class Upstream(McpServerConfig config)
        {
            public int ActiveCalls;
            public TaskCompletionSource Drained { get; set; } = _Completed();

            private static TaskCompletionSource _Completed()
            {
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                completion.SetResult();
                return completion;
            }

            public McpServerConfig Config { get; set; } = config;
            public ServerConnection? Connection { get; set; }
            public IReadOnlyList<Microsoft.Extensions.AI.AIFunction> Tools { get; set; } = [];
            public string? Issue { get; set; }
            public int Failures;
            public CancellationTokenSource Lifetime { get; } = new();
            public Channel<bool> Signals { get; } = Channel.CreateUnbounded<bool>();
            public Channel<bool> AccountChanges { get; } = Channel.CreateUnbounded<bool>();
            public TaskCompletionSource Initial { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource? Connecting { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public Task Worker { get; set; } = Task.CompletedTask;
        }
    }
}

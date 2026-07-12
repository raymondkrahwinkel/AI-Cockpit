using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Core.Configuration;

namespace Cockpit.Infrastructure.Sessions.Permissions;

/// <summary>
/// Hosts the single shared MCP permission-prompt server for the whole app: one Kestrel endpoint
/// on loopback exposing <c>mcp__cockpit__permission_prompt</c> over HTTP. Runs for the app's whole
/// lifetime as an <see cref="IHostedService"/>. On start it binds a (by default OS-assigned) free
/// port, writes the <c>--mcp-config</c> file every session points at, and publishes both via
/// <see cref="IPermissionServerState"/>.
/// </summary>
/// <remarks>
/// Transport choice: HTTP via the official ModelContextProtocol .NET SDK, verified end-to-end
/// against claude.exe 2.1.197 (allow lets a Write through, deny blocks it with is_error). The
/// coordinator singleton is shared into this inner web host so the tool resolves the same
/// instance the sessions resolve their decisions on.
/// </remarks>
internal sealed class PermissionMcpServer : IHostedService, IPermissionServerState, IAsyncDisposable
{
    private readonly IPermissionCoordinator _coordinator;
    private readonly ClaudeCliOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<PermissionMcpServer> _logger;
    private WebApplication? _app;
    private string? _mcpConfigPath;
    private string? _mcpUrl;

    public PermissionMcpServer(
        IPermissionCoordinator coordinator,
        IOptions<CockpitOptions> options,
        ILoggerFactory loggerFactory)
    {
        _coordinator = coordinator;
        _options = options.Value.Claude;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<PermissionMcpServer>();
    }

    public string? McpConfigPath => _mcpConfigPath;

    public string? PermissionPromptToolName { get; private set; }

    public string? PermissionMcpUrl => _mcpUrl;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(_loggerFactory);
        builder.Services.AddSingleton(_coordinator);
        builder.Services.AddSingleton<PermissionPromptTool>();
        builder.Services
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<PermissionPromptTool>();

        builder.WebHost.UseKestrel();
        builder.WebHost.UseUrls($"http://{_options.PermissionServerHost}:{_options.PermissionServerPort}");

        _app = builder.Build();
        _app.MapMcp("/mcp");

        await _app.StartAsync(cancellationToken).ConfigureAwait(false);

        var boundUrl = ResolveBoundUrl(_app);
        _mcpUrl = $"{boundUrl.TrimEnd('/')}/mcp";
        PermissionPromptToolName = $"mcp__{McpConfigFile.ServerName}__permission_prompt";

        // Seed the baseline (permission-only) config so McpConfigPath is valid before the first spawn; the
        // process rewrites it with the current registry fan-out at each spawn (ClaudeCliProcess).
        _mcpConfigPath = WriteConfigFile(McpConfigFile.Serialize(_mcpUrl));

        _logger.LogInformation(
            "MCP permission server listening at {McpUrl}; config written to {ConfigPath}",
            _mcpUrl,
            _mcpConfigPath);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app is not null)
        {
            await _app.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static string ResolveBoundUrl(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel did not expose its bound addresses.");

        return addresses.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("MCP permission server bound no address.");
    }

    private static string WriteConfigFile(string json)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Cockpit");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, "mcp-permission.json");
        File.WriteAllText(path, json);
        return path;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync().ConfigureAwait(false);
        }
    }
}

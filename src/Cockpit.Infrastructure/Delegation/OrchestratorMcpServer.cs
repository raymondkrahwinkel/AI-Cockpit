using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Delegation;

namespace Cockpit.Infrastructure.Delegation;

/// <summary>
/// Hosts the <c>cockpit-orchestrator</c> MCP server (#67): one Kestrel endpoint on loopback exposing the
/// delegation tools, running for the app's lifetime. Same shape as the permission server, which is the
/// in-process MCP hosting this app already proves works against the real CLI.
/// </summary>
/// <remarks>
/// The server is hosted whether or not anything uses it; a session only gets these tools if it was started with
/// delegation enabled, which is what keeps a delegated sub-agent from being handed the very tools it would need
/// to delegate on.
/// </remarks>
internal sealed class OrchestratorMcpServer : IHostedService, IOrchestratorServerState, ISingletonService, IAsyncDisposable
{
    /// <summary>The MCP server name; the tools appear to a session as <c>mcp__cockpit-orchestrator__delegate_task</c> and friends.</summary>
    public const string ServerName = "cockpit-orchestrator";

    private readonly IDelegationService _delegation;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<OrchestratorMcpServer> _logger;
    private WebApplication? _app;

    public OrchestratorMcpServer(IDelegationService delegation, ILoggerFactory loggerFactory)
    {
        _delegation = delegation;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<OrchestratorMcpServer>();
    }

    public string? OrchestratorMcpUrl { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(_loggerFactory);
        builder.Services.AddSingleton(_delegation);
        builder.Services.AddSingleton<OrchestratorTools>();
        builder.Services
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<OrchestratorTools>();

        builder.WebHost.UseKestrel();

        // Port 0: the OS picks a free one. A fixed port would collide with a second cockpit — and with whatever
        // else happens to hold it.
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        _app = builder.Build();
        _app.MapMcp("/mcp");

        await _app.StartAsync(cancellationToken).ConfigureAwait(false);

        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel did not expose its bound addresses.");
        var boundUrl = addresses.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("The orchestrator MCP server bound no address.");

        OrchestratorMcpUrl = $"{boundUrl.TrimEnd('/')}/mcp";
        _logger.LogInformation("Orchestrator MCP server listening at {McpUrl}", OrchestratorMcpUrl);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app is not null)
        {
            await _app.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync().ConfigureAwait(false);
        }
    }
}

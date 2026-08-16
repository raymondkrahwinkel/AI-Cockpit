using Material.Icons;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Core.Abstractions.Diagrams;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Plugin.Diagram;

// Plugin-schil proof (AC-809): a workspace panel plus a toolbar action, so both host surfaces are exercised.
public sealed class DiagramPlugin : ICockpitPlugin
{
    private const string WorkspaceTypeId = "diagram.panel";

    // Carries a confirmed quick-start (AC-816) from the toolbar action into the next fresh body this plugin's
    // own AddWorkspaceType factory builds. _lastSurfaceId tracks that body's surface so a second quick-start,
    // while the one proof-of-concept panel is still open, couples directly instead of being silently dropped —
    // OpenWorkspaceAsync only calls the factory again once the existing workspace of this type is gone.
    private DiagramQuickStart? _pendingQuickStart;
    private string? _lastSurfaceId;

    public PluginMetadata Metadata { get; } = new(
        Id: "diagram",
        DisplayName: "Diagram Builder",
        Author: "Cockpit",
        Description: "Renders a Mermaid-syntax diagram in a workspace panel.");

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        host.AddWorkspaceType(new WorkspaceTypeRegistration(WorkspaceTypeId, "Diagram", context =>
        {
            _lastSurfaceId = context.WorkspaceId;
            var quickStart = _pendingQuickStart;
            _pendingQuickStart = null;
            return new DiagramWorkspaceBody(context, host, quickStart);
        })
        {
            IconKind = MaterialIconKind.Sitemap,
            Description = "A diagram rendered from Mermaid syntax.",
        });

        host.AddToolbarAction(new ToolbarAction("Nieuw diagram", MaterialIconKind.Sitemap, () => _QuickStartAsync(host)));
    }

    private async Task _QuickStartAsync(ICockpitHost host)
    {
        var quickStart = await DiagramQuickStartDialog.ShowAsync(host, "Nieuw diagram");
        if (quickStart is null)
        {
            return;
        }

        var registry = host.Services.GetService(typeof(IDiagramAccessRegistry)) as IDiagramAccessRegistry;
        var reusingOpenSurface = _lastSurfaceId is { } surfaceId && registry?.Resolve(surfaceId) is not null;

        if (reusingOpenSurface)
        {
            // OpenWorkspaceAsync below only brings the still-open panel from an earlier quick-start to front —
            // its body will not be rebuilt, so couple this pick onto it directly rather than lose it to a body
            // that never runs. A stale _pendingQuickStart must not survive to a later, unrelated creation either.
            if (quickStart.SessionPaneId is { } sessionId)
            {
                registry!.Couple(sessionId, _lastSurfaceId!);
            }
        }
        else
        {
            _pendingQuickStart = quickStart;
        }

        await host.OpenWorkspaceAsync(WorkspaceTypeId);
    }

    public void Dispose()
    {
    }
}

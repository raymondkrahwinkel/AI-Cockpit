using Cockpit.Core.Abstractions;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.CompanionTools;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.Plugins;

/// <summary>
/// Holds the companion tools plugins register (<c>ICockpitHost.AddCompanionTool</c>), so the cockpit's pop-out
/// companion window can offer them. Same shape as <see cref="IWidgetRegistry"/>, not a view-model collection.
/// </summary>
public interface ICompanionToolRegistry
{
    /// <summary>
    /// Records a companion tool along with what its owning plugin brought: storage and the observe surface, the
    /// same pair a widget's registration carries. A tool id that is already registered is refused, first one wins.
    /// </summary>
    /// <returns>
    /// False when another plugin already contributes this tool id — the caller says so; nothing throws.
    /// </returns>
    bool Register(CompanionToolRegistration tool, IPluginStorage pluginStorage, ICockpitSessionObserver sessions, string? pluginId = null);

    /// <summary>
    /// Withdraws the tool <paramref name="toolId"/>; false when none is registered.
    /// </summary>
    bool Unregister(string toolId);

    /// <summary>
    /// Raised when a plugin contributes or withdraws a companion tool. Plugins initialize after the view models are built, so
    /// anything reading <see cref="Tools"/> would otherwise read an empty list once and never hear about tools
    /// that arrived later.
    /// </summary>
    event EventHandler? Changed;

    /// <summary>
    /// Every companion tool registered so far, in registration order — what the companion window lists.
    /// </summary>
    IReadOnlyList<CompanionToolRegistration> Tools { get; }

    /// <summary>
    /// Builds the context for <paramref name="toolId"/> from the storage/sessions its registering plugin brought,
    /// or null when no plugin contributes that id.
    /// </summary>
    ICompanionToolContext? CreateContext(string toolId);

    /// <summary>The owning plugin, for the host's enable action; null for built-in tools.</summary>
    string? GetPluginId(string toolId);

    /// <summary>
    /// The badge <paramref name="toolId"/>'s plugin sets through its context, or null when no plugin contributes
    /// that id. The strip shows it on the tool's button and follows its <see cref="SideMenuButtonBadge.Changed"/>.
    /// </summary>
    SideMenuButtonBadge? GetBadge(string toolId);

    /// <summary>
    /// Raised with the tool id when a tool asks for its pop-out (<see cref="ICompanionToolContext.RequestPopOut"/>).
    /// </summary>
    event EventHandler<string>? PopOutRequested;
}

internal sealed class CompanionToolRegistry : ICompanionToolRegistry, ISingletonService
{
    private readonly List<RegisteredCompanionTool> _tools = [];

    public event EventHandler? Changed;

    public event EventHandler<string>? PopOutRequested;

    public IReadOnlyList<CompanionToolRegistration> Tools => [.. _tools.Select(tool => tool.Registration)];

    // First registration of a tool id wins; a later duplicate is refused rather than added beside it, since two
    // plugins claiming the same id would silently double the companion window's entry.
    public bool Register(CompanionToolRegistration tool, IPluginStorage pluginStorage, ICockpitSessionObserver sessions, string? pluginId = null)
    {
        if (_tools.Any(existing => existing.Registration.Id == tool.Id))
        {
            return false;
        }

        _tools.Add(new RegisteredCompanionTool(tool, pluginStorage, sessions, pluginId));
        Changed?.Invoke(this, EventArgs.Empty);

        return true;
    }

    public bool Unregister(string toolId)
    {
        if (_tools.RemoveAll(tool => tool.Registration.Id == toolId) == 0)
        {
            return false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public ICompanionToolContext? CreateContext(string toolId)
    {
        if (_tools.FirstOrDefault(tool => tool.Registration.Id == toolId) is not { } registered)
        {
            return null;
        }

        return new CompanionToolContext(
            new CompanionToolInstanceStorage(registered.PluginStorage, toolId),
            registered.Sessions,
            registered.Badge,
            () => PopOutRequested?.Invoke(this, toolId));
    }

    public string? GetPluginId(string toolId) =>
        _tools.FirstOrDefault(tool => tool.Registration.Id == toolId)?.PluginId;

    public SideMenuButtonBadge? GetBadge(string toolId) =>
        _tools.FirstOrDefault(tool => tool.Registration.Id == toolId)?.Badge;
}

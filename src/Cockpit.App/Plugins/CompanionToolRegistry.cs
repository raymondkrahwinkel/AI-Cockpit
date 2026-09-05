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
    bool Register(CompanionToolRegistration tool, IPluginStorage pluginStorage, ICockpitSessionObserver sessions);

    /// <summary>
    /// Raised when a plugin contributes a companion tool. Plugins initialize after the view models are built, so
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
}

internal sealed class CompanionToolRegistry : ICompanionToolRegistry, ISingletonService
{
    private readonly List<RegisteredCompanionTool> _tools = [];

    public event EventHandler? Changed;

    public IReadOnlyList<CompanionToolRegistration> Tools => [.. _tools.Select(tool => tool.Registration)];

    // First registration of a tool id wins; a later duplicate is refused rather than added beside it, since two
    // plugins claiming the same id would silently double the companion window's entry.
    public bool Register(CompanionToolRegistration tool, IPluginStorage pluginStorage, ICockpitSessionObserver sessions)
    {
        if (_tools.Any(existing => existing.Registration.Id == tool.Id))
        {
            return false;
        }

        _tools.Add(new RegisteredCompanionTool(tool, pluginStorage, sessions));
        Changed?.Invoke(this, EventArgs.Empty);

        return true;
    }

    public ICompanionToolContext? CreateContext(string toolId)
    {
        if (_tools.FirstOrDefault(tool => tool.Registration.Id == toolId) is not { } registered)
        {
            return null;
        }

        return new CompanionToolContext(new CompanionToolInstanceStorage(registered.PluginStorage, toolId), registered.Sessions);
    }
}

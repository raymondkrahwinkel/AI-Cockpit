using Cockpit.Core.Abstractions;
using Cockpit.Plugins.Abstractions.CompanionTools;

namespace Cockpit.App.Plugins;

/// <summary>
/// Holds the companion tools plugins register (<c>ICockpitHost.AddCompanionTool</c>), so the cockpit's pop-out
/// companion window can offer them. Same shape as <see cref="IWidgetRegistry"/>, not a view-model collection.
/// </summary>
public interface ICompanionToolRegistry
{
    /// <summary>
    /// Records a companion tool. A tool id that is already registered is refused, first one wins.
    /// </summary>
    /// <returns>
    /// False when another plugin already contributes this tool id — the caller says so; nothing throws.
    /// </returns>
    bool Register(CompanionToolRegistration tool);

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
}

internal sealed class CompanionToolRegistry : ICompanionToolRegistry, ISingletonService
{
    private readonly List<CompanionToolRegistration> _tools = [];

    public event EventHandler? Changed;

    public IReadOnlyList<CompanionToolRegistration> Tools => [.. _tools];

    // First registration of a tool id wins; a later duplicate is refused rather than added beside it, since two
    // plugins claiming the same id would silently double the companion window's entry.
    public bool Register(CompanionToolRegistration tool)
    {
        if (_tools.Any(existing => existing.Id == tool.Id))
        {
            return false;
        }

        _tools.Add(tool);
        Changed?.Invoke(this, EventArgs.Empty);

        return true;
    }
}

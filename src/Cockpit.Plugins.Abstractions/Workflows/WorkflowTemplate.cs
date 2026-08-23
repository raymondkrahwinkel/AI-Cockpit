namespace Cockpit.Plugins.Abstractions.Workflows;

/// <summary>
/// A flow somebody already drew, offered as a starting point (#69). A plugin that contributes steps knows better
/// than anyone how they fit together.
/// </summary>
/// <remarks>
/// The flow is carried as the workflows plugin's own JSON (<see cref="Json"/>) — the same text a flow is
/// exported to and imported from. A template with JSON the workflows plugin cannot read is skipped with a
/// reason, never half-loaded.
/// </remarks>
/// <param name="Id">
/// Stable identity ("youtrack.ticket-to-branch"), so a template can be recognised across versions.
/// </param>
/// <param name="Name">
/// What the picker shows.
/// </param>
/// <param name="Description">
/// One line: what the flow does, in the operator's words.
/// </param>
/// <param name="Json">
/// The flow, as the workflows plugin writes it. Node ids inside are rewritten on import, so two copies of a template can live side by side.
/// </param>
/// <param name="Category">
/// The heading it is filed under; defaults to the contributing plugin's own name, which is where an operator looks for it.
/// </param>
public sealed record WorkflowTemplate(
    string Id,
    string Name,
    string Description,
    string Json,
    string? Category = null);

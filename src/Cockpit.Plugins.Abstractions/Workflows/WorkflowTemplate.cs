namespace Cockpit.Plugins.Abstractions.Workflows;

/// <summary>
/// A flow somebody already drew, offered as a starting point (#69). A plugin that contributes steps knows better than
/// anyone how they fit together — "a ticket you pick becomes a branch, an agent and a status change" is the flow the
/// YouTrack plugin exists to make possible, and leaving every operator to rediscover it from an empty canvas is
/// leaving the useful half unsaid.
/// <para>
/// The flow itself is carried as the workflows plugin's own JSON (<see cref="Json"/>) — the same text a flow is
/// exported to and imported from, so a template a plugin ships, a template shared as a file, and a flow you drew
/// yourself are one kind of thing rather than three. A template with JSON the workflows plugin cannot read is skipped
/// with a reason, never half-loaded.
/// </para>
/// </summary>
/// <param name="Id">Stable identity ("youtrack.ticket-to-branch"), so a template can be recognised across versions.</param>
/// <param name="Name">What the picker shows.</param>
/// <param name="Description">One line: what the flow does, in the operator's words.</param>
/// <param name="Json">The flow, as the workflows plugin writes it. Node ids inside are rewritten on import, so two copies of a template can live side by side.</param>
/// <param name="Category">The heading it is filed under; defaults to the contributing plugin's own name, which is where an operator looks for it.</param>
public sealed record WorkflowTemplate(
    string Id,
    string Name,
    string Description,
    string Json,
    string? Category = null);

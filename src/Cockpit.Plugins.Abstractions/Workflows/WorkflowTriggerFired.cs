namespace Cockpit.Plugins.Abstractions.Workflows;

/// <summary>A trigger a plugin fired, and the data the flow starts with (#69).</summary>
/// <param name="TypeId">The trigger's type id — <c>youtrack.picked</c>.</param>
/// <param name="Data">What happened, as fields the flow's later steps can refer to: the ticket, its summary, the branch name.</param>
public sealed record WorkflowTriggerFired(string TypeId, IReadOnlyDictionary<string, string> Data);

namespace Cockpit.Plugin.Workflows.Model;

/// <summary>
/// Whether a wire is allowed, and — when it is not — the sentence to show the operator. A bare "no" leaves them
/// dragging the same wire again wondering what the canvas has against them.
/// </summary>
public sealed record WorkflowConnectionRule(bool IsAllowed, string? Reason)
{
    public static WorkflowConnectionRule Allow() => new(true, null);

    public static WorkflowConnectionRule Refuse(string reason) => new(false, reason);
}

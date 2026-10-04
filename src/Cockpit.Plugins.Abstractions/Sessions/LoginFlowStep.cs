namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// One instruction in a running <see cref="ILoginFlow"/> — a message to show the operator, an optional link to
/// open, and whether the flow now waits on <see cref="ILoginFlow.SubmitAsync"/> before it can continue.
/// </summary>
/// <param name="Message">
/// The text to show, e.g. the CLI's own prompt.
/// </param>
/// <param name="LinkToOpen">
/// A URL the operator should visit, when this step names one.
/// </param>
/// <param name="AwaitsInput">
/// True when the flow is blocked on stdin — the host should offer an input field.
/// </param>
public sealed record LoginFlowStep(string Message, Uri? LinkToOpen, bool AwaitsInput)
{
    /// <summary>
    /// AC-1477: when the code this step shows stops being valid, if the flow knows. The host counts down to it.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

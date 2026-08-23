namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// A running login attempt for one profile, started via <see cref="TtyProviderRegistration.StartLogin"/> or
/// <see cref="SessionProviderRegistration.StartLogin"/>. Disposing before completion cancels the underlying process.
/// </summary>
public interface ILoginFlow : IAsyncDisposable
{
    /// <summary>
    /// The instruction(s) to show the operator, in the order the flow discovers them.
    /// </summary>
    IAsyncEnumerable<LoginFlowStep> Steps { get; }

    /// <summary>
    /// Forwards operator input to the flow; a no-op for a flow that needs nothing back.
    /// </summary>
    Task SubmitAsync(string value, CancellationToken cancellationToken);

    /// <summary>
    /// Completes once the underlying process exits, reporting whether the login succeeded.
    /// </summary>
    Task<LoginFlowResult> Completion { get; }
}

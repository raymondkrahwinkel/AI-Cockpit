namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// A running login attempt for one profile, started via <see cref="TtyProviderRegistration.StartLogin"/> or
/// <see cref="SessionProviderRegistration.StartLogin"/>. The host reads <see cref="Steps"/> to show the operator
/// what to do next, forwards anything they type to <see cref="SubmitAsync"/>, and awaits <see cref="Completion"/>
/// to know when the profile's login gate should be re-checked. Disposing before completion cancels the
/// underlying process.
/// </summary>
public interface ILoginFlow : IAsyncDisposable
{
    /// <summary>The instruction(s) to show the operator, in the order the flow discovers them.</summary>
    IAsyncEnumerable<LoginFlowStep> Steps { get; }

    /// <summary>
    /// Forwards operator input (e.g. a pasted authorization code) to the flow. A no-op for a flow that needs
    /// nothing back — Codex's device-code flow polls on its own.
    /// </summary>
    Task SubmitAsync(string value, CancellationToken cancellationToken);

    /// <summary>Completes once the underlying process exits, reporting whether the login succeeded.</summary>
    Task<LoginFlowResult> Completion { get; }
}

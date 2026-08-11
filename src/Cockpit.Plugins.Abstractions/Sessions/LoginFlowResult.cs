namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>The outcome of a completed <see cref="ILoginFlow"/> — the process's exit, not a re-check of the login gate.</summary>
/// <param name="Success">True when the underlying CLI reported a clean exit.</param>
/// <param name="ErrorMessage">Set when <paramref name="Success"/> is false, for display to the operator.</param>
public sealed record LoginFlowResult(bool Success, string? ErrorMessage);

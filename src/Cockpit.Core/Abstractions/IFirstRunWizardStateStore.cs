namespace Cockpit.Core.Abstractions;

/// <summary>
/// Persists the first-run wizard's completion marker (AC-509): the content version an install last completed or
/// skipped, so the gate in <c>App</c> knows whether to show the wizard again without re-showing it on every start.
/// </summary>
public interface IFirstRunWizardStateStore
{
    /// <summary>
    /// The version last completed or skipped, or null before the wizard has ever run.
    /// </summary>
    Task<int?> GetCompletedVersionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that the operator finished or dismissed the wizard at the given content version.
    /// </summary>
    Task MarkCompletedAsync(int version, CancellationToken cancellationToken = default);
}

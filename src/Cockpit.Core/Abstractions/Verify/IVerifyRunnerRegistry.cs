using Cockpit.Core.Verify;

namespace Cockpit.Core.Abstractions.Verify;

/// <summary>
/// The persistent registry of verify runners (AC-86), one section of <c>cockpit.json</c>. The source of truth for
/// which command the verify loop may run per project: the agent triggers a runner by name/directory, it never
/// supplies the command, so this is also the boundary that keeps "verify" from executing anything not registered.
/// </summary>
public interface IVerifyRunnerRegistry
{
    Task<IReadOnlyList<VerifyRunner>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Stores <paramref name="runner"/>, replacing any earlier runner with the same <see cref="VerifyRunner.Label"/> so an edit does not duplicate it.</summary>
    Task SaveAsync(VerifyRunner runner, CancellationToken cancellationToken = default);

    /// <summary>Removes the runner named <paramref name="label"/>; a no-op when none matches.</summary>
    Task RemoveAsync(string label, CancellationToken cancellationToken = default);
}

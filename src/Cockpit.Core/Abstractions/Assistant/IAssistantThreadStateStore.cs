namespace Cockpit.Core.Abstractions.Assistant;

/// <summary>
/// Which of the assistant's project threads are closed, kept across a restart beside its transcript log.
/// </summary>
public interface IAssistantThreadStateStore
{
    /// <summary>
    /// The closed threads' project ids; empty when nothing was ever closed or the file cannot be read.
    /// </summary>
    Task<IReadOnlyList<string>> LoadClosedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the saved set with <paramref name="projectIds"/>.
    /// </summary>
    Task SaveClosedAsync(IReadOnlyList<string> projectIds, CancellationToken cancellationToken = default);
}

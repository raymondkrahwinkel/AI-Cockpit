using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;

namespace Cockpit.Core.Tests.Sessions;

/// <summary>
/// The scheduled-resume store without a config file behind it. Shared by the tests that care about what the
/// coordinator does rather than where it writes.
/// </summary>
internal sealed class InMemoryScheduledResumeStore : IScheduledResumeStore
{
    public List<ScheduledResume> Saved { get; set; } = [];

    public Task<IReadOnlyList<ScheduledResume>> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ScheduledResume>>(Saved);

    public Task SaveAsync(IReadOnlyList<ScheduledResume> resumes, CancellationToken cancellationToken = default)
    {
        Saved = [.. resumes];
        return Task.CompletedTask;
    }
}

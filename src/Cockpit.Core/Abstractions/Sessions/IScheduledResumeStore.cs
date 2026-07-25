using Cockpit.Core.Sessions;

namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// Where pending resumes live between now and their moment (AC-234). On disk rather than in memory, because the
/// window a resume exists to cover is precisely the one where the cockpit may be closed — an allowance that rolls
/// over at 07:30 is no use to a schedule that died when the app did.
/// </summary>
public interface IScheduledResumeStore
{
    /// <summary>Every resume still waiting, oldest moment first. Empty when none are scheduled.</summary>
    Task<IReadOnlyList<ScheduledResume>> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Replaces the stored set. Callers load, change, and save the whole list — there are only ever a handful.</summary>
    Task SaveAsync(IReadOnlyList<ScheduledResume> resumes, CancellationToken cancellationToken = default);
}

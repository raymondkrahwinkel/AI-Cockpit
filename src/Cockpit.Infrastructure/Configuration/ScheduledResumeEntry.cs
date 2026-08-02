using Cockpit.Core.Sessions;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of a `ScheduledResume` in the `scheduledResumes` section of `cockpit.json`.
internal sealed class ScheduledResumeEntry
{
    public string PaneId { get; set; } = string.Empty;

    public DateTimeOffset DueAt { get; set; }

    public string Prompt { get; set; } = string.Empty;

    public string? Reason { get; set; }

    public static ScheduledResumeEntry FromDomain(ScheduledResume resume) => new()
    {
        PaneId = resume.PaneId,
        DueAt = resume.DueAt,
        Prompt = resume.Prompt,
        Reason = resume.Reason,
    };

    public ScheduledResume ToDomain() => new(PaneId, DueAt, Prompt, Reason);
}

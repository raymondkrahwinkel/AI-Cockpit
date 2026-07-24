using Cockpit.Core.Sessions;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>
/// On-disk shape of a <see cref="ScheduledResume"/> in the <c>scheduledResumes</c> section of <c>cockpit.json</c>.
/// </summary>
internal sealed class ScheduledResumeEntry
{
    public string PaneId { get; set; } = string.Empty;

    public string? ConversationId { get; set; }

    public DateTimeOffset DueAt { get; set; }

    public string Prompt { get; set; } = string.Empty;

    public string? Reason { get; set; }

    public static ScheduledResumeEntry FromDomain(ScheduledResume resume) => new()
    {
        PaneId = resume.PaneId,
        ConversationId = resume.ConversationId,
        DueAt = resume.DueAt,
        Prompt = resume.Prompt,
        Reason = resume.Reason,
    };

    public ScheduledResume ToDomain() => new(PaneId, ConversationId, DueAt, Prompt, Reason);
}

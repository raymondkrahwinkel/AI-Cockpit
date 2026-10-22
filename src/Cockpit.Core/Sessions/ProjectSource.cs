namespace Cockpit.Core.Sessions;

// How a transcript row came to belong to a project (assistant threads). Per project, so a row in two threads can
// say why it is in each: the operator's own choice wins over anything derived.
public enum ProjectSource
{
    None,

    // The operator put it there: typed in that thread, dragged or moved it.
    Explicit,

    // The session the row came from belongs to the project.
    Session,

    TicketPrefix,

    // The assistant said so: set_thread, tag_prompt or a question's projectIds.
    AssistantTag,

    // The row carries no project of its own and follows the prompt of its turn. Never written to the log.
    Inherited,

    // The prompt names the project, which the host read before the turn ran (ProjectInference).
    Mentioned,
}

// One project's share of a row that spans several (assistant threads § 9.1): a span of a prompt, or a block of a reply.
public sealed record TranscriptProjectPart(string ProjectId, int Start, int Length);

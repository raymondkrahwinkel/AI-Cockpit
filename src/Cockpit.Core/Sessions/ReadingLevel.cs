namespace Cockpit.Core.Sessions;

/// <summary>
/// How much of an SDK/chat session's transcript is shown, so one running session can be read by
/// a developer or by a non-technical operator without changing what the agent does (AC-138). Only
/// SDK/chat sessions have a reading level — a TTY session is a raw terminal and shows everything.
/// </summary>
public enum ReadingLevel
{
    /// <summary>Everything: tool calls, costs, jargon — the full developer surface. The default.</summary>
    Developer,

    /// <summary>
    /// Calm but complete: runs of auto-executed tool calls fold into one expandable "N steps run" line
    /// and the standalone token/cost meter gives way to the usage pill, while every power is still reachable.
    /// </summary>
    Focus,

    /// <summary>
    /// For anyone: no tool noise, no cost or model chip, jargon in plain words. Tool calls that asked for
    /// approval — pending or already allowed/denied — stay visible at this level too, in human language.
    /// </summary>
    Simple,
}

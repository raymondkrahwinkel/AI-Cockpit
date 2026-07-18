namespace Cockpit.Core.Voice;

/// <summary>How read-aloud (#35) renders an assistant reply before speaking it. A user choice, no imposed default; a fresh install starts on <see cref="Verbatim"/>.</summary>
public enum ReadAloudMode
{
    /// <summary>Speak the extracted prose as-is — code/tables stripped, paths/URLs spoken — with no LLM pass.</summary>
    Verbatim,

    /// <summary>Rewrite the reply into natural spoken sentences via the local LLM first, tagging language runs for pronunciation.</summary>
    Naturalized,

    /// <summary>Summarize the reply to its essence via the local LLM first, while preserving every number, decision, warning and action item.</summary>
    Summarized,
}

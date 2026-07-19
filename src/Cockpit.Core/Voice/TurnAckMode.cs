namespace Cockpit.Core.Voice;

/// <summary>
/// How a turn-start acknowledgement is produced (AC-99) — the short "let me take a look" spoken the moment the
/// agent starts working, so a voice conversation is not met with silence while it thinks.
/// </summary>
public enum TurnAckMode
{
    /// <summary>No acknowledgement is spoken.</summary>
    Off,

    /// <summary>A rotating preset phrase is spoken — instant, no model call.</summary>
    InstantPhrases,

    /// <summary>The local LLM writes a short contextual acknowledgement, falling back to a preset when it is slow or unavailable.</summary>
    LocalLlm,
}

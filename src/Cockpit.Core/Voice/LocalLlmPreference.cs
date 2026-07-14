namespace Cockpit.Core.Voice;

/// <summary>
/// Which local model server the transcript cleanup prefers when auto-detect finds more than one running. Only
/// consulted while auto-detect is on; a specific choice is tried first and the machine falls back to whatever
/// else is detected if that one is not actually serving.
/// </summary>
public enum LocalLlmPreference
{
    /// <summary>No preference — use whichever detected server is heaviest (most likely the one holding a model).</summary>
    Auto,

    /// <summary>Prefer Ollama when it is running.</summary>
    Ollama,

    /// <summary>Prefer LM Studio when it is running.</summary>
    LmStudio,
}

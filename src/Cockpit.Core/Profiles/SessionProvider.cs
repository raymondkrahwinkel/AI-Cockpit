namespace Cockpit.Core.Profiles;

/// <summary>
/// Which backend drives a session started under a profile (#26). Fixed when a profile is created —
/// changing provider means a new profile — so credentials/config never end up inconsistent.
/// </summary>
public enum SessionProvider
{
    /// <summary>The <c>claude</c> CLI in stream-json mode (native tools, permissions, live control).</summary>
    ClaudeCli,

    /// <summary>A local Ollama server over its OpenAI-compatible <c>/v1</c> endpoint.</summary>
    Ollama,

    /// <summary>A local LM Studio server over its OpenAI-compatible <c>/v1</c> endpoint.</summary>
    LmStudio,
}

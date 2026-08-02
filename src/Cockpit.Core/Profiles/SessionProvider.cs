namespace Cockpit.Core.Profiles;

// Which backend drives a session started under a profile (#26). Fixed when a profile is created —
// changing provider means a new profile — so credentials/config never end up inconsistent.
public enum SessionProvider
{
    // The `claude` CLI in stream-json mode (native tools, permissions, live control).
    ClaudeCli,

    // A local Ollama server over its OpenAI-compatible `/v1` endpoint.
    Ollama,

    // A local LM Studio server over its OpenAI-compatible `/v1` endpoint.
    LmStudio,

    // A provider registered by a plugin (#45) — see `PluginProviderConfig`.
    Plugin,
}

namespace Cockpit.Core.Profiles;

// What the `claude` CLI needs to run under a profile: its config directory (`CLAUDE_CONFIG_DIR`, holding
// `.credentials.json`/`.claude.json`; `~/.claude` default handled specially by the Claude provider plugin at spawn)
// and optionally a specific executable (`null` resolves the bundled/default one). Formerly first-class `SessionProfile` fields.
public sealed record ClaudeConfig(string ConfigDir, string? ExecutablePath = null)
    : ProviderConfig(SessionProvider.ClaudeCli);

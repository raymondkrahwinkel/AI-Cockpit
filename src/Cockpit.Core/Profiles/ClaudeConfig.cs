namespace Cockpit.Core.Profiles;

// What the `claude` CLI needs to run under a profile: its own configuration directory, and optionally a
// specific executable.
//
// These used to be first-class fields on `SessionProfile`, with a `null` provider
// config meaning "this is a Claude profile". That is the shape of an application that grew around one provider:
// Claude was what a profile was unless it said otherwise, and every other provider had to announce itself. Now
// Claude announces itself too.
//
// `ConfigDir`:
// The directory used as `CLAUDE_CONFIG_DIR` for a session under this profile, holding its
// `.credentials.json` and `.claude.json`. The CLI's default (`~/.claude`) is a valid value and is
// treated specially at spawn time by the provider plugin that owns the Claude machinery.
// `ExecutablePath`:
// Executable to spawn. `null` means "resolve the bundled/default executable at spawn time".
public sealed record ClaudeConfig(string ConfigDir, string? ExecutablePath = null)
    : ProviderConfig(SessionProvider.ClaudeCli);

namespace Cockpit.Core.Profiles;

/// <summary>
/// What the <c>claude</c> CLI needs to run under a profile: its own configuration directory, and optionally a
/// specific executable.
/// <para>
/// These used to be first-class fields on <see cref="SessionProfile"/>, with a <see langword="null"/> provider
/// config meaning "this is a Claude profile". That is the shape of an application that grew around one provider:
/// Claude was what a profile was unless it said otherwise, and every other provider had to announce itself. Now
/// Claude announces itself too.
/// </para>
/// </summary>
/// <param name="ConfigDir">
/// The directory used as <c>CLAUDE_CONFIG_DIR</c> for a session under this profile, holding its
/// <c>.credentials.json</c> and <c>.claude.json</c>. The CLI's default (<c>~/.claude</c>) is a valid value and is
/// treated specially at spawn time by the provider plugin that owns the Claude machinery.
/// </param>
/// <param name="ExecutablePath">
/// Executable to spawn. <see langword="null"/> means "resolve the bundled/default executable at spawn time".
/// </param>
public sealed record ClaudeConfig(string ConfigDir, string? ExecutablePath = null)
    : ProviderConfig(SessionProvider.ClaudeCli);

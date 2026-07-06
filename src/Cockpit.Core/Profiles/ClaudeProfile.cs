namespace Cockpit.Core.Profiles;

/// <summary>
/// A distinct <c>claude</c> CLI identity: its own config/credentials directory (and therefore
/// its own login), optionally its own executable. Lets the cockpit run several independent
/// accounts (e.g. personal + work) side by side without mixing their state.
/// </summary>
/// <param name="Label">Display name shown in the profile picker and on a session panel.</param>
/// <param name="ConfigDir">
/// Directory used as <c>CLAUDE_CONFIG_DIR</c> for a session spawned under this profile.
/// Holds that profile's <c>.credentials.json</c> and <c>.claude.json</c>.
/// </param>
/// <param name="ExecutablePath">
/// Executable to spawn. <see langword="null"/> means "resolve the bundled/default executable
/// at spawn time" (see <see cref="IClaudeExecutableLocator"/>).
/// </param>
/// <param name="Purpose">Short free-text description of what this profile is for.</param>
/// <param name="Defaults">
/// Start defaults (mode/model/effort) the New-session dialog pre-selects for this profile.
/// <see langword="null"/> falls back to the app defaults.
/// </param>
public sealed record ClaudeProfile(
    string Label,
    string ConfigDir,
    string? ExecutablePath = null,
    string? Purpose = null,
    ProfileDefaults? Defaults = null);

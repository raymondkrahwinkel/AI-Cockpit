using Cockpit.Core.Profiles;

namespace Cockpit.Core.Abstractions.Claude;

/// <summary>
/// Launches the real interactive <c>claude</c> TUI inside a ConPTY for TTY mode (#9). Reuses the
/// same profile/executable/trust plumbing as the SDK-mode spawn (<c>ClaudeCliProcess</c>): resolves
/// the bundled executable, pre-marks the working directory trusted under the profile, and composes
/// the environment (inherited parent env + <c>CLAUDE_CONFIG_DIR</c> + <c>TERM</c>). Unlike SDK mode
/// it spawns <c>claude</c> plainly — no <c>-p</c>/stream-json flags — so the genuine TUI runs.
/// </summary>
public interface IClaudeTtyLauncher
{
    /// <summary>
    /// Starts <c>claude</c> in a pseudo console sized <paramref name="columns"/>×<paramref name="rows"/>
    /// under <paramref name="profile"/> (or the host's own config when null).
    /// </summary>
    IConPtyProcess Launch(ClaudeProfile? profile, short columns, short rows);
}

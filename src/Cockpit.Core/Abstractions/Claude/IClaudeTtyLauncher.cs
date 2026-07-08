using Cockpit.Core.Profiles;

namespace Cockpit.Core.Abstractions.Claude;

/// <summary>
/// Launches the real interactive <c>claude</c> TUI inside a pseudo console/pty for TTY mode (#9)
/// (ConPTY on Windows, Porta.Pty on Linux/macOS — see <c>IPtyHostFactory</c>). Reuses the same
/// profile/executable/trust plumbing as the SDK-mode spawn (<c>ClaudeCliProcess</c>): resolves the
/// bundled executable, pre-marks the working directory trusted under the profile, and composes the
/// environment (inherited parent env + <c>CLAUDE_CONFIG_DIR</c> + <c>TERM</c>). Unlike SDK mode it
/// never adds <c>-p</c>/stream-json flags, so the genuine TUI runs — but it does pass the
/// permission-mode/model/effort chosen in the New-session dialog as launch-only start defaults
/// (mirroring <c>ClaudeCliProcess.BuildArguments</c>); once running, the TUI itself owns any live
/// switching (<c>/model</c>, <c>/effort</c>, Shift+Tab) since TTY mode has no control channel. Also
/// forces the CLI's own session id via <c>--session-id</c>, so the cockpit knows the exact live
/// transcript path (<c>&lt;config-dir&gt;/projects/*/&lt;session-id&gt;.jsonl</c>) for read-aloud
/// tailing (#35b).
/// </summary>
public interface IClaudeTtyLauncher
{
    /// <summary>
    /// Starts <c>claude</c> in a pseudo console sized <paramref name="columns"/>×<paramref name="rows"/>
    /// under <paramref name="profile"/> (or the host's own config when null), forcing its session id to
    /// <paramref name="sessionId"/> (<c>--session-id</c>) so the transcript file backing read-aloud
    /// tailing is known up front, with <paramref name="permissionMode"/>/<paramref name="model"/>/
    /// <paramref name="effort"/> as its launch-only start defaults (any of which may be null/blank to
    /// omit that flag).
    /// </summary>
    IConPtyProcess Launch(
        ClaudeProfile? profile,
        Guid sessionId,
        string? permissionMode,
        string? model,
        string? effort,
        short columns,
        short rows);
}

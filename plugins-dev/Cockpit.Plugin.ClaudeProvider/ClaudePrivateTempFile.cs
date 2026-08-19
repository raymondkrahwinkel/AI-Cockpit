namespace Cockpit.Plugin.ClaudeProvider;

// The per-spawn files this plugin hands the CLI by path instead of by value: the --mcp-config and the
// --append-system-prompt-file. Both carry something that is nobody else's business — the mcp-config can hold a
// user API-key server's bearer header, the system prompt holds the assistant's memory and whatever standing
// instruction a profile gave it — so both are written owner-only (AC-63) rather than at the umask, and both are
// deleted by whoever owns the session's lifetime (the SDK driver on dispose, the host for the TTY route).
//
// One writer for both, because the protection is the part that must not drift: a second copy of this method is
// a second place for the mode bits to be forgotten.
internal static class ClaudePrivateTempFile
{
    // A system prompt on the command line is the one argument that grows without a ceiling — the standing
    // instruction plus the operator's own memory and current-state files — and both platforms refuse it well
    // before it stops being reasonable: Windows caps the whole command line at 32.767 characters
    // (`CreateProcess`), Linux caps a single argument at 131.072 (`MAX_ARG_STRLEN`). Measured on Windows with
    // the real flag set: a 32.400-character prompt spawns, 32.876 fails with "the filename or extension is too
    // long" — and it fails at `CreateProcess`, so there is no process, no stderr, and nothing to read but a
    // session that never came up. Handing it over as a file takes the command line to ~450 characters whatever
    // the prompt weighs, which is why this is not conditional on the platform or on a length: one path, so the
    // path that carries an operator's own growing memory is also the one that is exercised every launch.
    public static string? WriteSystemPrompt(string? prompt) =>
        string.IsNullOrWhiteSpace(prompt) ? null : Write("cockpit-claude-prompt", ".md", prompt);

    // The file (and its directory) are 0600/0700 on Unix, set at create time so the content never exists at the
    // umask's permissions; on Windows the per-user temp profile is the protection, exactly as the host's
    // `TtyMcpConfigFile` / `CockpitConfigPath` treat it (this plugin cannot reference Infrastructure, so it
    // mirrors the pattern).
    public static string Write(string directoryName, string extension, string contents)
    {
        var directory = Path.Combine(Path.GetTempPath(), directoryName);
        Directory.CreateDirectory(directory);
        _Restrict(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var path = Path.Combine(directory, $"{Guid.NewGuid():N}{extension}");
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            // Set at create time, so the file never exists at the umask's permissions with the content already in it.
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(path, options))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(contents);
        }

        _Restrict(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }

    // Best-effort unlink of a file written above: a session that has ended has no business leaving its prompt or
    // its bearer header on disk, and a temp file that cannot be removed must never surface out of a teardown.
    public static void Delete(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // A locked/already-gone temp file is not worth failing a dispose over.
        }
    }

    // A no-op on Windows, which has no Unix mode bits — there the per-user temp profile is the protection. Guarding
    // inside the method (not at the call site) is what keeps the SetUnixFileMode call off the Windows analysis path.
    private static void _Restrict(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, mode);
        }
        catch (Exception)
        {
            // A filesystem that carries no Unix permissions (a mounted share, a container volume) is not a reason to
            // fail the launch — the write is what matters, and UnixCreateMode already set the mode where it is honoured.
        }
    }
}

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
    public const string McpDirectory = "cockpit-claude-mcp";

    public const string PromptDirectory = "cockpit-claude-prompt";

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
        string.IsNullOrWhiteSpace(prompt) ? null : Write(PromptDirectory, ".md", prompt);

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

    // The directories the two writers above use. Named here rather than at the call sites because the sweep below
    // has to know all of them: a third file written past this list would be swept by nothing.
    private static readonly string[] Directories = [McpDirectory, PromptDirectory];

    // How long a file may sit here before the sweep treats it as left behind (AC-956). A session does not outlive
    // the cockpit that started it, so anything from before yesterday belongs to a run that is over — and a
    // generous window is what keeps this from ever racing a session that is still using its file.
    private static readonly TimeSpan StaleAfter = TimeSpan.FromDays(1);

    // Clears what sessions that were killed rather than closed left behind (AC-956) — the same job
    // `ClaudeStatusLine.SweepStale` does for the statusline snapshots, called from the same place at plugin start.
    //
    // *Why a sweep is needed at all, when both routes already delete on teardown.* They do, and both deletes are
    // the last thing their teardown does — after the subprocess is killed and its pumps are awaited. The app's
    // shutdown gives that teardown a bounded budget and then hard-exits (`Program.DisposeCockpit`), so a teardown
    // that runs long is cut off exactly at its tail. Measured 19-08: 30 mcp-configs going back five days, 28 of
    // them holding a literal bearer header, plus a promptfile holding the assistant's whole memory. A deadline can
    // be tuned; it cannot be relied on. This is the backstop that does not depend on one.
    public static void SweepStale()
    {
        foreach (var directoryName in Directories)
        {
            SweepStale(Path.Combine(Path.GetTempPath(), directoryName), StaleAfter);
        }
    }

    // The sweep itself, against a directory it is handed — so the rule can be asserted directly instead of against
    // the machine's own temp, where a test would be deleting whatever a real cockpit left there.
    internal static void SweepStale(string directory, TimeSpan staleAfter)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                try
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > staleAfter)
                    {
                        File.Delete(file);
                    }
                }
                catch (Exception)
                {
                    // A file another cockpit is holding open is swept on a later start.
                }
            }
        }
        catch (Exception)
        {
            // Housekeeping never fails a launch.
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

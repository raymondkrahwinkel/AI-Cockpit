namespace Cockpit.Infrastructure.Configuration;

/// <summary>
/// Resolves where the cockpit keeps its state (<c>%APPDATA%\Cockpit</c> on Windows, <c>~/.config/Cockpit</c>
/// elsewhere) and owns the file permissions that go with it.
/// <para>
/// The permissions live here rather than at each call site because the files hold credentials — API keys, MCP
/// bearer headers, plugin tokens — and a default <c>File.Create</c> leaves them at whatever the umask says,
/// which is world-readable on a stock Fedora. Every writer of a credential-bearing file goes through
/// <see cref="WriteAllTextPrivate"/> or <see cref="CreatePrivateFile"/>, so "who may read this" is one
/// decision in one place instead of one that each new writer has to remember.
/// </para>
/// </summary>
internal static class CockpitConfigPath
{
    /// <summary>Owner read/write. No group, no other — these files hold credentials.</summary>
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>Owner read/write/traverse. Without the execute bit the owner cannot enter their own directory.</summary>
    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Cockpit");

    public static string Default => Path.Combine(Root, "cockpit.json");

    /// <summary>The plugins root — a <c>plugins/</c> folder next to <c>cockpit.json</c>, stable across app updates. Each plugin lives in its own subfolder here.</summary>
    public static string PluginsRoot => Path.Combine(Root, "plugins");

    /// <summary>Creates <paramref name="directory"/> if needed and restricts it to its owner. Idempotent.</summary>
    public static void EnsurePrivateDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        Restrict(directory, PrivateDirectoryMode);
    }

    /// <summary>
    /// Opens <paramref name="path"/> for writing, truncating it, created owner-only. The mode is set as part of
    /// the create call, so there is no window in which the file exists at the umask's permissions with content
    /// already in it — and an <em>existing</em> file (one written by a version of the cockpit that did not do
    /// this) is restricted on the way past, which is what migrates the operator's current world-readable config.
    /// </summary>
    public static FileStream CreatePrivateFile(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            EnsurePrivateDirectory(directory);
        }

        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
        };

        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = PrivateFileMode;
        }

        var stream = new FileStream(path, options);
        Restrict(path, PrivateFileMode);

        return stream;
    }

    /// <summary>
    /// Restricts a file the cockpit wrote before it knew better. This is what migrates an operator's existing
    /// world-readable <c>cockpit.json</c> without asking them to run <c>chmod</c> on our behalf.
    /// </summary>
    public static void RestrictExistingFile(string path)
    {
        if (File.Exists(path))
        {
            Restrict(path, PrivateFileMode);
        }
    }

    /// <summary>Writes <paramref name="contents"/> to an owner-only file. See <see cref="CreatePrivateFile"/>.</summary>
    public static void WriteAllTextPrivate(string path, string contents, bool flushToDisk = false)
    {
        using var stream = CreatePrivateFile(path);
        using (var writer = new StreamWriter(stream, leaveOpen: true))
        {
            writer.Write(contents);
        }

        if (flushToDisk)
        {
            // The bytes reach the disk before anything renames this file over the operator's config. Without it the
            // rename can outlive its own content across a power cut: the directory entry points at a file the disk
            // has not written yet, and "atomic" becomes a promise the hardware never made.
            stream.Flush(flushToDisk: true);
        }
    }

    /// <summary>
    /// Replaces <paramref name="path"/> with <paramref name="contents"/> in one step, keeping a <c>.bak</c> of what
    /// was there. Used by the encryption migration, which rewrites every credential in the file at once: a crash
    /// halfway through a plain write leaves a truncated config, and a truncated config is the operator's
    /// credentials gone. Writing a sibling file and renaming it means the file is either entirely the old one or
    /// entirely the new one — a rename is atomic — and the backup is the way back if the new one is wrong.
    /// </summary>
    public static void ReplaceAtomicallyPrivate(string path, string contents)
    {
        // A sidecar of its own, never a shared name. Two writers on one fixed "<path>.new" is how the operator's
        // config was destroyed on 2026-07-14: the second writer truncated the first one's half-written file and
        // wrote its shorter document into it, and the first went on writing at its own offsets — leaving a valid
        // document with the tail of a longer one behind it. Then one of them renamed that into place. The rename
        // was atomic all along; the file it renamed was the problem. (The other writer, finding its sidecar gone,
        // threw FileNotFoundException — the same race, wearing a different mask.)
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.new";
        try
        {
            WriteAllTextPrivate(temporaryPath, contents, flushToDisk: true);

            if (File.Exists(path))
            {
                // Replace() is the atomic swap, and it writes the backup as part of the same operation.
                File.Replace(temporaryPath, path, path + ".bak", ignoreMetadataErrors: true);
                RestrictExistingFile(path + ".bak");
            }
            else
            {
                File.Move(temporaryPath, path);
            }

            RestrictExistingFile(path);
        }
        finally
        {
            // A failed write must not leave its sidecar behind: a unique name means a crash would otherwise litter
            // the config directory with one file per attempt, forever.
            if (File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                    // Swept on the next start (SweepStaleSidecars) — a locked leftover is not worth failing a save over.
                }
            }
        }
    }

    /// <summary>
    /// Removes the sidecars a killed or crashed write left behind. Called once at startup: they are dead weight,
    /// they hold the same secrets as the config, and nothing reads them.
    /// </summary>
    public static void SweepStaleSidecars(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return;
        }

        foreach (var stale in Directory.EnumerateFiles(directory, $"{Path.GetFileName(path)}.*.new"))
        {
            try
            {
                File.Delete(stale);
            }
            catch (IOException)
            {
                // Best effort: a leftover we cannot remove is untidy, not dangerous.
            }
        }
    }

    /// <summary>
    /// Applies <paramref name="mode"/> to an existing file or directory. A no-op on Windows, which has no Unix
    /// mode bits — there the equivalent protection is the per-user profile directory itself, and pretending
    /// otherwise by throwing would break the platform that does not need this.
    /// </summary>
    private static void Restrict(string path, UnixFileMode mode)
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
            // A file on a filesystem that carries no permissions (a mounted share, a container volume) is not a
            // reason to refuse to save the operator's settings. The write itself is what matters most.
        }
    }
}

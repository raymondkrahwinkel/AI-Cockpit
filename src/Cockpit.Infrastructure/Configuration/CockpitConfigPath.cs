using Cockpit.Core.Configuration;

namespace Cockpit.Infrastructure.Configuration;

// Resolves where the cockpit keeps its state (`%APPDATA%\Cockpit` on Windows, `~/.config/Cockpit`
// elsewhere — a development build keeps its own, see `CockpitBuild`) and owns the file permissions
// that go with it.
//
// The permissions live here rather than at each call site because the files hold credentials — API keys, MCP
// bearer headers, plugin tokens — and a default `File.Create` leaves them at whatever the umask says,
// which is world-readable on a stock Fedora. Every writer of a credential-bearing file goes through
// `WriteAllTextPrivate` or `CreatePrivateFile`, so "who may read this" is one
// decision in one place instead of one that each new writer has to remember.
internal static class CockpitConfigPath
{
    // Owner read/write. No group, no other — these files hold credentials.
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    // Owner read/write/traverse. Without the execute bit the owner cannot enter their own directory.
    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public static string Root => CockpitBuild.StateRoot;

    public static string Default => Path.Combine(Root, "cockpit.json");

    // The plugins root — a `plugins/` folder next to `cockpit.json`, stable across app updates. Each plugin lives in its own subfolder here.
    public static string PluginsRoot => Path.Combine(Root, "plugins");

    // Where project logos live (AC-162): a `project-logos/` folder next to `cockpit.json`. The picked
    // file or downloaded image is copied here and the project keeps the copy's path, so a logo survives the
    // original being moved, renamed or unplugged — a card that quietly loses its picture is worse than one that
    // never had it.
    public static string ProjectLogosRoot => Path.Combine(Root, "project-logos");

    // Where session-isolation worktrees live (AC-85): a `worktrees/` folder next to `cockpit.json`,
    // grouped per repository. Under the app state root, so a development build keeps its own (`CockpitBuild`)
    // and a worktree is never checked out inside the repository being worked on.
    public static string WorktreesRoot => Path.Combine(Root, "worktrees");

    // Where repositories cloned from a URL live (AC-90): a `clones/` folder next to `cockpit.json`,
    // one directory per repository under a `host/org/repo` slug. Under the app state root, so a development
    // build keeps its own (`CockpitBuild`) rather than cloning into the production cockpit's area.
    public static string ClonesRoot => Path.Combine(Root, "clones");

    // The voice assistant's own memory (AC-595): a markdown file next to `cockpit.json`, holding what the
    // operator told it to keep. Plain markdown on purpose — there is no UI for it, so opening it is the way to
    // prune it.
    public static string AssistantMemory => Path.Combine(Root, "assistant-memory.md");

    // Where the assistant leaves the conversation before restarting itself (AC-596). Its own file rather than a
    // section of the memory: one is appended to and the other overwritten, and separate files need no parser and
    // no lock between them.
    public static string AssistantCurrentState => Path.Combine(Root, "assistant-state.md");

    // What the operator saw in the assistant window (AC-684): a JSON snapshot next to `cockpit.json`, overwritten
    // whole on every new transcript row — the conversation's current shape, not a trail with its own retention.
    public static string AssistantTranscript => Path.Combine(Root, "assistant-transcript.json");

    // Creates `directory` if needed and restricts it to its owner. Idempotent.
    public static void EnsurePrivateDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        Restrict(directory, PrivateDirectoryMode);
    }

    // Opens `path` for writing, truncating it, created owner-only. The mode is set as part of
    // the create call, so there is no window in which the file exists at the umask's permissions with content
    // already in it — and an *existing* file (one written by a version of the cockpit that did not do
    // this) is restricted on the way past, which is what migrates the operator's current world-readable config.
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

    // Restricts a file the cockpit wrote before it knew better. This is what migrates an operator's existing
    // world-readable `cockpit.json` without asking them to run `chmod` on our behalf.
    public static void RestrictExistingFile(string path)
    {
        if (File.Exists(path))
        {
            Restrict(path, PrivateFileMode);
        }
    }

    // Writes `contents` to an owner-only file. See `CreatePrivateFile`.
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

    // Replaces `path` with `contents` in one step, keeping a `.bak` of what
    // was there. Used by the encryption migration, which rewrites every credential in the file at once: a crash
    // halfway through a plain write leaves a truncated config, and a truncated config is the operator's
    // credentials gone. Writing a sibling file and renaming it means the file is either entirely the old one or
    // entirely the new one — a rename is atomic — and the backup is the way back if the new one is wrong.
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

    // Removes the sidecars a killed or crashed write left behind. Called once at startup: they are dead weight,
    // they hold the same secrets as the config, and nothing reads them.
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

    // Applies `mode` to an existing file or directory. A no-op on Windows, which has no Unix
    // mode bits — there the equivalent protection is the per-user profile directory itself, and pretending
    // otherwise by throwing would break the platform that does not need this.
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

using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>
/// Repairs this process's PATH when a GUI or AppImage launch stripped it to the system defaults (AC-19). A login
/// shell puts the user's bin directories (<c>~/.local/bin</c> and friends) on PATH; a desktop-file or AppImage
/// launch does not, and every child this process spawns — a provider CLI, git, gh, node — inherits that truncated
/// PATH, so tools read "not found" even though they are installed. The per-locator fallbacks (Claude/Codex) cover
/// only their own executables; this is the root fix, run once at startup before anything resolves a tool or
/// spawns a session.
/// <para>
/// Three steps: detect the truncated case (a user bin directory exists on disk but is missing from PATH — a
/// terminal launch never looks like that, so the normal path costs nothing), ask the login shell for its PATH
/// under a hard timeout, and fall back to prepending the well-known user bin directories when the shell cannot
/// answer. Whichever source wins, the user bin directories that exist end up on PATH. Windows is exempt: it
/// inherits the user+system PATH from the registry whatever launches the app.
/// </para>
/// </summary>
public static class StartupPathRepair
{
    private const string PathVariable = "PATH";

    // Guards the PATH line against shell-init noise (motd, plugin echoes): only the line carrying the marker is
    // the answer, whatever else the login init prints around it. Internal for testing.
    internal const string Marker = "__COCKPIT_LOGIN_PATH__=";

    // A login shell that takes longer than this is wedged on its init (a prompt plugin, a network mount) — the
    // fallback list is good enough, and startup must not hang on it.
    private static readonly TimeSpan LoginShellTimeout = TimeSpan.FromSeconds(3);

    public static void Run(ILogger logger)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            _Repair(logger);
        }
        catch (Exception exception)
        {
            // The repair never keeps the cockpit from starting; at worst the per-locator fallbacks still apply.
            logger.LogWarning(exception, "Could not repair the process PATH; continuing with the inherited one.");
        }
    }

    private static void _Repair(ILogger logger)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            return;
        }

        var currentPath = Environment.GetEnvironmentVariable(PathVariable) ?? string.Empty;
        var userBins = UserBinDirectories(home).Where(Directory.Exists).ToList();
        if (!userBins.Any(directory => !ContainsEntry(currentPath, directory)))
        {
            // A terminal launch, or a machine without user bin directories: nothing to repair, no shell to spawn.
            return;
        }

        var loginShellPath = _ReadLoginShellPath(logger);
        var repaired = loginShellPath is null ? currentPath : MergePaths(loginShellPath, currentPath);

        // Belt and suspenders over either source: a shell that keeps its PATH export in interactive-only init
        // (.zshrc) answers the login probe without the user bins — make sure the ones that exist are on.
        repaired = PrependMissingEntries(repaired, userBins);

        if (repaired == currentPath)
        {
            return;
        }

        ProcessEnvironment.Assign(PathVariable, repaired);
        if (loginShellPath is null)
        {
            logger.LogWarning(
                "GUI/AppImage launch detected and the login shell did not answer; prepended the well-known user bin directories. PATH is now: {Path}",
                repaired);
        }
        else
        {
            logger.LogInformation(
                "GUI/AppImage launch detected; repaired the truncated PATH from the login shell. PATH is now: {Path}",
                repaired);
        }
    }

    // ~/.local/bin (pipx, uv, the claude installer's launcher — on macOS too), ~/.bun/bin (bun installs, codex),
    // ~/bin (the classic). The same directories the per-locator fallbacks know, minus the executable-specific ones.
    internal static IEnumerable<string> UserBinDirectories(string home)
    {
        yield return Path.Combine(home, ".local", "bin");
        yield return Path.Combine(home, ".bun", "bin");
        yield return Path.Combine(home, "bin");

        if (OperatingSystem.IsMacOS())
        {
            // A Finder/launchd launch gets the bare system PATH (/usr/bin:/bin:/usr/sbin:/sbin) — Homebrew's
            // directories are the practical user bins there: /opt/homebrew/bin on Apple Silicon, /usr/local/bin
            // on Intel. On Linux /usr/local/bin is already in every default PATH, so this stays macOS-only.
            yield return "/opt/homebrew/bin";
            yield return "/usr/local/bin";
        }
    }

    internal static bool ContainsEntry(string path, string directory)
    {
        var wanted = _Normalize(directory);

        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(entry => _Normalize(entry) == wanted);
    }

    // The login shell's PATH first (it carries the user's own ordering), then whatever the truncated PATH had
    // that the shell does not know about (an AppImage mount directory, ~/.dotnet/tools) — deduplicated, first
    // occurrence wins.
    internal static string MergePaths(string loginShellPath, string currentPath)
    {
        var seen = new HashSet<string>();
        var merged = new List<string>();
        foreach (var entry in loginShellPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                     .Concat(currentPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)))
        {
            if (seen.Add(_Normalize(entry)))
            {
                merged.Add(entry);
            }
        }

        return string.Join(Path.PathSeparator, merged);
    }

    internal static string PrependMissingEntries(string path, IEnumerable<string> directories)
    {
        var missing = directories.Where(directory => !ContainsEntry(path, directory)).ToList();
        if (missing.Count == 0)
        {
            return path;
        }

        if (path.Length > 0)
        {
            missing.Add(path);
        }

        return string.Join(Path.PathSeparator, missing);
    }

    // Pulls the marked PATH out of the login shell's output; the last marker line wins (an init that echoes the
    // probe command would put an earlier, unexpanded copy in the stream). Null when no marker line carries a value.
    internal static string? ExtractMarkedPath(string output)
    {
        string? marked = null;
        foreach (var line in output.Split('\n'))
        {
            var index = line.LastIndexOf(Marker, StringComparison.Ordinal);
            if (index >= 0)
            {
                marked = line[(index + Marker.Length)..].Trim();
            }
        }

        return string.IsNullOrEmpty(marked) ? null : marked;
    }

    private static string? _ReadLoginShellPath(ILogger logger)
    {
        // SHELL is not guaranteed for a launchd-started GUI app on macOS — fall back to the platform default
        // login shell (zsh since Catalina) rather than /bin/sh, whose init would miss the user's PATH exports.
        var shell = Environment.GetEnvironmentVariable("SHELL");
        if (string.IsNullOrWhiteSpace(shell))
        {
            shell = OperatingSystem.IsMacOS() ? "/bin/zsh" : "/bin/sh";
        }

        return ReadLoginShellPath(shell, LoginShellTimeout, logger);
    }

    // Internal for testing: the timeout is the one hard promise this probe makes, so the tests drive it with a
    // fake shell and a short deadline instead of trusting the comment.
    internal static string? ReadLoginShellPath(string shell, TimeSpan timeout, ILogger logger)
    {
        try
        {
            // -l -c, not interactive: an interactive shell can hang on prompt/tty setup, and login init is where
            // PATH exports belong. "$PATH" is quoted so fish joins its path list with colons; sh/bash/zsh are
            // unaffected by the quotes.
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = shell,
                ArgumentList = { "-l", "-c", $"echo \"{Marker}$PATH\"" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null)
            {
                return null;
            }

            // Both pipes are drained async so a chatty init cannot fill one and wedge the shell against it.
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();

            // One deadline covers both waits: the shell's exit AND the stdout read (a background child the init
            // left behind can hold the pipe open past the shell's own exit). Two full waits in a row would
            // double the promised ceiling.
            var deadline = Stopwatch.StartNew();
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                process.Kill(entireProcessTree: true);
                logger.LogDebug(
                    "The login shell {Shell} did not answer within {Timeout}; using the fallback list.",
                    shell, timeout);

                return null;
            }

            var remaining = timeout - deadline.Elapsed;
            if (remaining < TimeSpan.Zero || !standardOutput.Wait(remaining))
            {
                return null;
            }

            // The marker line is the answer even when the init exited non-zero — a broken plugin does not
            // invalidate the PATH the shell built before it.
            return ExtractMarkedPath(standardOutput.Result);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not read the login shell PATH from {Shell}; using the fallback list.", shell);

            return null;
        }
    }

    // Trailing-slash tolerance: "/home/x/.local/bin/" and "/home/x/.local/bin" are the same PATH entry.
    private static string _Normalize(string entry) => entry.Length > 1 ? entry.TrimEnd('/') : entry;
}

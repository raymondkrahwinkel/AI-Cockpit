using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.FileSystemGlobbing;
using ModelContextProtocol.Server;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Shell;
using Cockpit.Core.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Provisioning;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Plugins.Shared;

namespace Cockpit.Infrastructure.Sessions;

// AC-2247: bounded coding tools share the calling session's guardrail policy.
internal sealed class CodingTools(ISessionRegistry sessions, IShellCommandRunner runner, IOrgPolicy? orgPolicy = null)
{
    internal const string EndpointName = "cockpit-coding";
    internal const string ShellEndpointName = "cockpit-coding-shell";

    internal static bool StaysNative(string serverName) =>
        string.Equals(serverName, EndpointName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(serverName, ShellEndpointName, StringComparison.OrdinalIgnoreCase);

    internal const int MaxLines = 2_000;
    internal const int MaxLineChars = 2_000;
    internal const int MaxChars = 51_200;
    private const int MaxResults = 100;

    // A file larger than this is not searched: it is a build artefact or data, not source.
    private const long MaxGrepFileBytes = 1_048_576;

    private static readonly TimeSpan GitListTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    [McpServerTool(Name = "read", ReadOnly = true)]
    [Description("Reads a text file in your session-allowed paths as numbered lines (`N: text`). Reads at most 2000 lines or 50 KiB per call and says where to continue with `offset`. Find the right file and line first with grep or glob, then read only the part you need instead of whole files.")]
    public string Read(
        [Description("The file, relative to your working directory or absolute in a session-allowed path.")] string filePath,
        [Description("The line to start at, 1-based. Default 1.")] int? offset = null,
        [Description("How many lines to read. Default and maximum 2000.")] int? limit = null)
    {
        if (!_TryResolve(filePath, out var path, out var error, out var warning))
        {
            return error;
        }

        return WithWarning(warning, _Read(path, filePath, offset, limit));
    }

    private static string _Read(string path, string filePath, int? offset, int? limit)
    {
        if (!File.Exists(path))
        {
            return Directory.Exists(path) ? $"Error: {filePath} is a directory; use glob to list it." : $"Error: no such file: {filePath}";
        }

        var start = Math.Max(1, offset ?? 1);
        var count = Math.Clamp(limit ?? MaxLines, 1, MaxLines);
        var output = new StringBuilder();
        var total = 0;
        var last = start - 1;
        var full = false;
        foreach (var line in File.ReadLines(path))
        {
            total++;
            if (full || total < start || total >= start + count)
            {
                continue;
            }

            var shown = line.Length > MaxLineChars ? $"{line[..MaxLineChars]}… (line cut at {MaxLineChars} chars)" : line;
            var entry = $"{total}: {shown}\n";
            if (output.Length + entry.Length > MaxChars)
            {
                full = true;
                continue;
            }

            output.Append(entry);
            last = total;
        }

        if (total == 0)
        {
            return "(End of file — 0 lines)";
        }

        if (start > total)
        {
            return $"Error: offset {start} is past the end of the file ({total} lines).";
        }

        output.Append(last < total
            ? $"(Showing lines {start}-{last} of {total}. Use offset={last + 1} to continue.)"
            : $"(End of file — {total} lines)");
        return output.ToString();
    }

    [McpServerTool(Name = "glob", ReadOnly = true)]
    [Description("Finds files in your session-allowed paths by glob pattern, e.g. `**/*.cs` or `src/**/Session*.cs`. Respects .gitignore in a git repository. Returns at most 100 paths.")]
    public async Task<string> Glob(
        [Description("The glob pattern, relative to `path`.")] string pattern,
        [Description("The directory to search in, relative to your working directory or absolute in a session-allowed path. Default: the working directory itself.")] string? path = null)
    {
        if (!_TryResolve(path ?? ".", out var directory, out var error, out var warning))
        {
            return error;
        }

        return WithWarning(warning, await _GlobAsync(directory, pattern).ConfigureAwait(false));
    }

    private async Task<string> _GlobAsync(string directory, string pattern)
    {
        var workingRoot = Path.GetFullPath(sessions.Find(McpRequestContext.CurrentPaneId ?? string.Empty)?.WorkingDirectory ?? directory);
        var root = PathBoundary.Evaluate(directory, [workingRoot]).IsInside ? workingRoot : directory;
        var matcher = new Matcher(_PathComparison).AddInclude(pattern);
        var files = (await _ListFilesAsync(root).ConfigureAwait(false))
            .Select(file => Path.GetFullPath(Path.Combine(root, file)))
            .Where(file => PathBoundary.Evaluate(file, [directory]).IsInside && File.Exists(file))
            .Select(file => Path.GetRelativePath(directory, file).Replace('\\', '/'))
            .Where(file => matcher.Match(file).HasMatches)
            .ToList();
        return _Listing(files.Take(MaxResults), files.Count, "files");
    }

    [McpServerTool(Name = "grep", ReadOnly = true)]
    [Description("Searches file contents in your session-allowed paths with a regular expression (.NET syntax). Returns at most 100 matches as `path:line: text`. Use `include` to narrow by file name, e.g. `*.cs`. Respects .gitignore in a git repository.")]
    public async Task<string> Grep(
        [Description("The regular expression to search for.")] string pattern,
        [Description("The directory to search in, relative to your working directory or absolute in a session-allowed path. Default: the working directory itself.")] string? path = null,
        [Description("A glob the file path must match, e.g. `*.cs` or `src/**/*.ts`.")] string? include = null)
    {
        if (!_TryResolve(path ?? ".", out var directory, out var error, out var warning))
        {
            return error;
        }

        return WithWarning(warning, await _GrepAsync(directory, pattern, include).ConfigureAwait(false));
    }

    private async Task<string> _GrepAsync(string directory, string pattern, string? include)
    {
        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.None, RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            return $"Error: invalid regular expression: {ex.Message}";
        }

        var workingRoot = Path.GetFullPath(sessions.Find(McpRequestContext.CurrentPaneId ?? string.Empty)?.WorkingDirectory ?? directory);
        var root = PathBoundary.Evaluate(directory, [workingRoot]).IsInside ? workingRoot : directory;
        var matcher = include is null ? null : new Matcher(_PathComparison).AddInclude(include.Contains('/') ? include : $"**/{include}");
        var matches = new List<string>();
        var total = 0;
        foreach (var file in await _ListFilesAsync(root).ConfigureAwait(false))
        {
            var full = Path.GetFullPath(Path.Combine(root, file));
            var relative = file.Replace('\\', '/');
            if (!PathBoundary.Evaluate(full, [directory]).IsInside || matcher?.Match(relative).HasMatches == false || !PathBoundary.Evaluate(full, [root]).IsInside || !_IsSearchable(full))
            {
                continue;
            }

            try
            {
                var number = 0;
                foreach (var line in File.ReadLines(full))
                {
                    number++;
                    if (!regex.IsMatch(line))
                    {
                        continue;
                    }

                    total++;
                    if (matches.Count < MaxResults)
                    {
                        matches.Add($"{relative}:{number}: {(line.Length > MaxLineChars ? line[..MaxLineChars] + "…" : line)}");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RegexMatchTimeoutException)
            {
                continue;
            }
        }

        return _Listing(matches, total, "matches");
    }

    [McpServerTool(Name = "edit", ReadOnly = false, Destructive = false)]
    [Description("Replaces an exact piece of text in a file in your session-allowed paths. `oldString` must match exactly once, unless `replaceAll` is true; read the file first so it does. An empty `oldString` creates a new file with `newString` as its content. Line endings follow the file.")]
    public string Edit(
        [Description("The file, relative to your working directory or absolute in a session-allowed path.")] string filePath,
        [Description("The exact text to replace, including its indentation.")] string oldString,
        [Description("The text to put in its place.")] string newString,
        [Description("Replace every occurrence instead of exactly one. Default false.")] bool replaceAll = false)
    {
        if (!_TryResolve(filePath, out var path, out var error, out var warning))
        {
            return error;
        }

        return WithWarning(warning, _Edit(path, filePath, oldString, newString, replaceAll));
    }

    private static string _Edit(string path, string filePath, string oldString, string newString, bool replaceAll)
    {
        if (oldString.Length == 0)
        {
            if (File.Exists(path))
            {
                return $"Error: {filePath} already exists; an empty oldString only creates a new file.";
            }

            return _Write(path, filePath, newString);
        }

        if (!File.Exists(path))
        {
            return $"Error: no such file: {filePath}";
        }

        var bytes = File.ReadAllBytes(path);
        var hasBom = bytes is [0xEF, 0xBB, 0xBF, ..];
        var text = new UTF8Encoding(false).GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var from = oldString.ReplaceLineEndings(newline);
        var to = newString.ReplaceLineEndings(newline);

        var occurrences = _Count(text, from);
        if (occurrences == 0)
        {
            return $"Error: oldString was not found in {filePath}. Read the file and copy the text exactly, indentation included.";
        }

        if (occurrences > 1 && !replaceAll)
        {
            return $"Error: oldString occurs {occurrences} times in {filePath}. Add surrounding lines to make it unique, or set replaceAll.";
        }

        var index = text.IndexOf(from, StringComparison.Ordinal);
        var updated = replaceAll ? text.Replace(from, to, StringComparison.Ordinal) : string.Concat(text.AsSpan(0, index), to, text.AsSpan(index + from.Length));
        File.WriteAllText(path, updated, new UTF8Encoding(hasBom));
        return $"Edited {filePath}: {(replaceAll ? occurrences : 1)} replacement(s).";
    }

    [McpServerTool(Name = "write", ReadOnly = false, Destructive = false)]
    [Description("Writes a file in your session-allowed paths, creating its directories, and replaces it if it exists. Prefer edit for a change to an existing file.")]
    public string Write(
        [Description("The file, relative to your working directory or absolute in a session-allowed path.")] string filePath,
        [Description("The complete content of the file.")] string content)
    {
        if (!_TryResolve(filePath, out var path, out var error, out var warning))
        {
            return error;
        }

        return WithWarning(warning, _Write(path, filePath, content));
    }

    private static string _Write(string path, string filePath, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? path);
        File.WriteAllText(path, content);
        return $"Wrote {filePath}: {content.Length} chars.";
    }

    // Off retains the original working-directory boundary.
    internal static bool TryResolve(string? root, string relative, out string path, out string error)
    {
        path = string.Empty;
        if (root is null)
        {
            error = "Error: this tool works only in a cockpit session with a working directory.";
            return false;
        }

        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!PathBoundary.Evaluate(full, [root]).IsInside)
        {
            error = $"Error: {relative} is outside your working directory ({root}).";
            return false;
        }

        path = full;
        error = string.Empty;
        return true;
    }

    private static StringComparison _PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private bool _TryResolve(string relative, out string path, out string error, out string warning) =>
        TryResolveFor(sessions.Find(McpRequestContext.CurrentPaneId ?? string.Empty), relative, orgPolicy?.Current, out path, out error, out warning);

    internal static bool TryResolveFor(ISessionHandle? session, string relative, ProvisionPolicy? policy, out string path, out string error, out string warning)
    {
        warning = string.Empty;
        var root = session?.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(root))
        {
            return TryResolve(null, relative, out path, out error);
        }

        var environment = SessionGuardrailEnvironment.Build(session?.ActiveProfile, root,
            SessionScratch.PathFor(session?.PaneId ?? McpRequestContext.CurrentPaneId ?? string.Empty), null, policy, _ => { });
        if (environment["COCKPIT_GUARD_MODE"] == GuardrailMode.Off.ToString())
        {
            return TryResolve(root, relative, out path, out error);
        }

        path = Path.GetFullPath(Path.Combine(root, relative));
        error = string.Empty;
        if (PathBoundary.Evaluate(path, environment["COCKPIT_GUARD_ROOTS"].Split(Path.PathSeparator)).IsInside)
        {
            return true;
        }
        if (environment["COCKPIT_GUARD_MODE"] == GuardrailMode.Warn.ToString())
        {
            warning = $"guardrail: {path} is outside the session boundary";
            return true;
        }

        error = $"Error: {relative} is outside the session boundary (worktree, session scratch and allowed paths).";
        return false;
    }

    // AC-2250: these tools run as children of the cockpit, so only the calling session's own job counts as owned.
    internal static GuardDecision KillDecision(ISessionHandle? session, string paneId, string command, string directory, ProvisionPolicy? policy) =>
        CommandGuard.EvaluateKill(command, CommandGuard.SessionScope(null, paneId, directory,
                string.IsNullOrEmpty(paneId) ? null : SessionScratch.JobNameOf(SessionScratch.PathFor(paneId))),
            OrgPolicyCeilings.GuardrailMode(policy, session?.ActiveProfile?.GuardrailMode ?? SessionProfileEntry.ExistingProfileGuardrailMode));

    internal static string WithWarning(string warning, string result) =>
        warning.Length == 0 ? result : $"{warning}\n{result}";

    private static int _Count(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static bool _IsSearchable(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > MaxGrepFileBytes)
        {
            return false;
        }

        Span<byte> head = stackalloc byte[4096];
        using var stream = info.OpenRead();
        var read = stream.Read(head);
        return !head[..read].Contains((byte)0);
    }

    // The files under `root`, relative to it: what git tracks or would track in a repository (.gitignore holds, as for
    // ripgrep in opencode; -z keeps unusual names unquoted), otherwise every file below the root, links not followed.
    private async Task<IReadOnlyList<string>> _ListFilesAsync(string root)
    {
        if (Directory.Exists(Path.Combine(root, ".git")) || File.Exists(Path.Combine(root, ".git")))
        {
            var listed = await runner.RunAsync(root, "git", ["ls-files", "-z", "--cached", "--others", "--exclude-standard"], GitListTimeout).ConfigureAwait(false);
            if (listed.ExitCode == 0)
            {
                return [.. listed.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries)];
            }
        }

        return [.. Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).Select(file => Path.GetRelativePath(root, file))];
    }

    private static string _Listing(IEnumerable<string> shown, int total, string noun)
    {
        var lines = shown.ToList();
        if (lines.Count == 0)
        {
            return $"No {noun} found.";
        }

        var text = string.Join('\n', lines);
        return total > lines.Count ? $"{text}\n(Results truncated: showing {lines.Count} of {total} {noun}. Narrow the pattern or the path.)" : text;
    }
}

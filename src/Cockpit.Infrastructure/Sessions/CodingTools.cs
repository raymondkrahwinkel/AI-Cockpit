using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.FileSystemGlobbing;
using ModelContextProtocol.Server;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Shell;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Infrastructure.Sessions;

// The `cockpit-coding` MCP tools (AC-1489): a small coding toolset with opencode's names and shapes, for a model on
// an HTTP provider that would otherwise read whole files through the filesystem server. Every path stays inside the
// calling pane's working directory, and every answer is bounded, so a read cannot flood the context.
internal sealed class CodingTools(ISessionRegistry sessions, IShellCommandRunner runner)
{
    internal const int MaxLines = 2_000;
    internal const int MaxLineChars = 2_000;
    internal const int MaxChars = 51_200;
    private const int MaxResults = 100;

    // A file larger than this is not searched: it is a build artefact or data, not source.
    private const long MaxGrepFileBytes = 1_048_576;

    private static readonly TimeSpan GitListTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    [McpServerTool(Name = "read", ReadOnly = true)]
    [Description("Reads a text file in your working directory as numbered lines (`N: text`). Reads at most 2000 lines or 50 KiB per call and says where to continue with `offset`. Find the right file and line first with grep or glob, then read only the part you need instead of whole files.")]
    public string Read(
        [Description("The file, relative to your working directory or absolute inside it.")] string filePath,
        [Description("The line to start at, 1-based. Default 1.")] int? offset = null,
        [Description("How many lines to read. Default and maximum 2000.")] int? limit = null)
    {
        if (!_TryResolve(filePath, out var path, out var error))
        {
            return error;
        }

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
    [Description("Finds files in your working directory by glob pattern, e.g. `**/*.cs` or `src/**/Session*.cs`. Respects .gitignore in a git repository. Returns at most 100 paths.")]
    public async Task<string> Glob(
        [Description("The glob pattern, relative to `path`.")] string pattern,
        [Description("The directory to search in, relative to your working directory. Default: the working directory itself.")] string? path = null)
    {
        if (!_TryResolve(path ?? ".", out var directory, out var error))
        {
            return error;
        }

        var root = _Root() ?? directory;
        var matcher = new Matcher(_PathComparison).AddInclude(pattern);
        var files = (await _ListFilesAsync(root).ConfigureAwait(false))
            .Select(file => Path.GetFullPath(Path.Combine(root, file)))
            .Where(file => _IsUnder(file, directory) && File.Exists(file))
            .Select(file => Path.GetRelativePath(directory, file).Replace('\\', '/'))
            .Where(file => matcher.Match(file).HasMatches)
            .ToList();
        return _Listing(files.Take(MaxResults), files.Count, "files");
    }

    [McpServerTool(Name = "grep", ReadOnly = true)]
    [Description("Searches file contents in your working directory with a regular expression (.NET syntax). Returns at most 100 matches as `path:line: text`. Use `include` to narrow by file name, e.g. `*.cs`. Respects .gitignore in a git repository.")]
    public async Task<string> Grep(
        [Description("The regular expression to search for.")] string pattern,
        [Description("The directory to search in, relative to your working directory. Default: the working directory itself.")] string? path = null,
        [Description("A glob the file path must match, e.g. `*.cs` or `src/**/*.ts`.")] string? include = null)
    {
        if (!_TryResolve(path ?? ".", out var directory, out var error))
        {
            return error;
        }

        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.None, RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            return $"Error: invalid regular expression: {ex.Message}";
        }

        var root = _Root() ?? directory;
        var matcher = include is null ? null : new Matcher(_PathComparison).AddInclude(include.Contains('/') ? include : $"**/{include}");
        var matches = new List<string>();
        var total = 0;
        foreach (var file in await _ListFilesAsync(root).ConfigureAwait(false))
        {
            var full = Path.GetFullPath(Path.Combine(root, file));
            var relative = file.Replace('\\', '/');
            if (!_IsUnder(full, directory) || matcher?.Match(relative).HasMatches == false || _LinksOutside(full, root) || !_IsSearchable(full))
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
    [Description("Replaces an exact piece of text in a file in your working directory. `oldString` must match exactly once, unless `replaceAll` is true; read the file first so it does. An empty `oldString` creates a new file with `newString` as its content. Line endings follow the file.")]
    public string Edit(
        [Description("The file, relative to your working directory or absolute inside it.")] string filePath,
        [Description("The exact text to replace, including its indentation.")] string oldString,
        [Description("The text to put in its place.")] string newString,
        [Description("Replace every occurrence instead of exactly one. Default false.")] bool replaceAll = false)
    {
        if (!_TryResolve(filePath, out var path, out var error))
        {
            return error;
        }

        if (oldString.Length == 0)
        {
            if (File.Exists(path))
            {
                return $"Error: {filePath} already exists; an empty oldString only creates a new file.";
            }

            return Write(filePath, newString);
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
    [Description("Writes a file in your working directory, creating its directories, and replaces it if it exists. Prefer edit for a change to an existing file.")]
    public string Write(
        [Description("The file, relative to your working directory or absolute inside it.")] string filePath,
        [Description("The complete content of the file.")] string content)
    {
        if (!_TryResolve(filePath, out var path, out var error))
        {
            return error;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? path);
        File.WriteAllText(path, content);
        return $"Wrote {filePath}: {content.Length} chars.";
    }

    // The calling pane's working directory. The pane is the transport-verified one only: for file access the caller
    // may not name its own pane, so a call without one is refused rather than given a default.
    internal static string? RootFor(ISessionRegistry sessions) =>
        McpRequestContext.CurrentPaneId is { } pane && sessions.Find(pane)?.WorkingDirectory is { Length: > 0 } directory
            ? Path.GetFullPath(directory)
            : null;

    // Resolves `relative` against `root` and refuses anything that lands outside it, through `..`, an absolute path,
    // or a symlink or junction on the way whose target lies elsewhere.
    internal static bool TryResolve(string? root, string relative, out string path, out string error)
    {
        path = string.Empty;
        if (root is null)
        {
            error = "Error: this tool works only in a cockpit session with a working directory.";
            return false;
        }

        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!_IsUnder(full, root) || _LinksOutside(full, root))
        {
            error = $"Error: {relative} is outside your working directory ({root}).";
            return false;
        }

        path = full;
        error = string.Empty;
        return true;
    }

    private static StringComparison _PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private string? _Root() => RootFor(sessions);

    private bool _TryResolve(string relative, out string path, out string error) => TryResolve(_Root(), relative, out path, out error);

    private static bool _IsUnder(string path, string root)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(root);
        return path.Equals(trimmed, _PathComparison)
            || path.StartsWith(trimmed + Path.DirectorySeparatorChar, _PathComparison);
    }

    // Walks from the path up to (not including) the root; a dangling link counts too, since writing through one
    // would create its target wherever it points.
    private static bool _LinksOutside(string path, string root)
    {
        var rootLength = Path.TrimEndingDirectorySeparator(root).Length;
        for (var current = path; current is not null && current.Length > rootLength; current = Path.GetDirectoryName(current))
        {
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not { } link)
            {
                continue;
            }

            var target = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                ?? Path.GetFullPath(link, Path.GetDirectoryName(current) ?? root);
            if (!_IsUnder(target, root))
            {
                return true;
            }
        }

        return false;
    }

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

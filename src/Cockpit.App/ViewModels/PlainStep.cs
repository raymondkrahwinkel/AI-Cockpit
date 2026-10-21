using System.Text.Json;

namespace Cockpit.App.ViewModels;

// AC-1530: a tool call as one step in plain words at the Simple level — what was done, what is being done, and the
// folder it happened in, relative to the working directory. A `count` above one is a run of reads in one folder, or of
// calls to one MCP server.
public sealed record PlainStep(string Done, string Running, string? Folder, string? FolderLabel)
{
    public string Sentence => FolderLabel is null ? Done : $"{Done} in {FolderLabel}";

    public static PlainStep Describe(string? toolName, string? inputJson, string? workingDirectory, int count = 1)
    {
        var file = FileOf(toolName, inputJson);
        var name = file is null ? "a file" : Path.GetFileName(file);
        return toolName switch
        {
            "Read" when count > 1 => _InFolder($"Read {count} files", $"Reading {count} files", Path.GetDirectoryName(file), workingDirectory),
            "Read" => _InFolder($"Read {name}", $"Reading {name}", Path.GetDirectoryName(file), workingDirectory),
            "Glob" or "Grep" => _InFolder("Looked for files", "Looking for files", _Input(inputJson, "path"), workingDirectory),
            "Write" => _InFolder($"Created {name}", $"Creating {name}", Path.GetDirectoryName(file), workingDirectory),
            "Edit" or "MultiEdit" or "NotebookEdit" => new($"Changed {name}", $"Changing {name}", null, null),
            "Bash" => new("Ran a command", "Running a command", null, null),
            "WebFetch" or "WebSearch" => new("Looked something up online", "Looking something up online", null, null),
            "Task" or "Agent" => new("Asked a helper", "Asking a helper", null, null),
            _ when ServerOf(toolName) is { } server && count > 1 => new($"Used {server} {count} times", $"Using {server}", null, null),
            _ when ServerOf(toolName) is { } server => new($"Used {server}", $"Using {server}", null, null),
            null or "" => new("Used a tool", "Using a tool", null, null),
            _ => new($"Used {toolName}", $"Using {toolName}", null, null),
        };
    }

    // The MCP server an `mcp__server__tool` call went to; null for any other call.
    public static string? ServerOf(string? toolName) =>
        toolName?.Split("__") is ["mcp", { Length: > 0 } server, ..] ? server : null;

    // The file a Read, Write or Edit-family call names, as the call wrote it; null for any other call.
    public static string? FileOf(string? toolName, string? inputJson) => toolName switch
    {
        "Read" or "Write" or "Edit" or "MultiEdit" => _Input(inputJson, "file_path"),
        "NotebookEdit" => _Input(inputJson, "notebook_path"),
        _ => null,
    };

    // A folder relative to the working directory, its parts joined by " › "; null for the working directory itself.
    public static string? LabelOf(string folder, string? workingDirectory)
    {
        var relative = workingDirectory is { Length: > 0 } && folder.Length > 0 ? Path.GetRelativePath(workingDirectory, folder) : folder;
        if (relative is "." or "")
        {
            return null;
        }

        var shown = relative.StartsWith("..", StringComparison.Ordinal) ? folder : relative;
        return string.Join(" › ", shown.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries));
    }

    // The full path of something a call named, resolved against the working directory when the call gave it relative.
    public static string FullPath(string path, string? workingDirectory) =>
        workingDirectory is { Length: > 0 } ? Path.Combine(workingDirectory, path) : path;

    private static PlainStep _InFolder(string done, string running, string? folder, string? workingDirectory)
    {
        if (string.IsNullOrEmpty(folder))
        {
            return new(done, running, null, null);
        }

        var full = FullPath(folder, workingDirectory);
        return LabelOf(full, workingDirectory) is { } label ? new(done, running, full, label) : new(done, running, null, null);
    }

    private static string? _Input(string? inputJson, string key)
    {
        if (string.IsNullOrWhiteSpace(inputJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(inputJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(key, out var value)
                && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 } text
                ? text
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

// AC-1530: the green block after a turn that made or changed files — one line per file, and the folder they share.
public sealed record TurnResult(string Title, IReadOnlyList<TurnResultFile> Files, string Folder)
{
    // Null when the turn wrote nothing that succeeded: a failed or refused write made no file to show.
    public static TurnResult? From(IEnumerable<TranscriptEntryViewModel> turnRows, string? workingDirectory)
    {
        var written = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in turnRows)
        {
            if (row is { IsToolUse: true, HasResult: true, IsResultError: false } && PlainStep.FileOf(row.ToolName, row.InputJson) is { } file
                && row.ToolName != "Read")
            {
                var path = PlainStep.FullPath(file, workingDirectory);
                written[path] = written.GetValueOrDefault(path) || row.ToolName == "Write";
            }
        }

        if (written.Count == 0)
        {
            return null;
        }

        var paths = written.Keys.ToList();
        var folder = _Parent(paths[0]);
        while (folder is not null && !paths.TrueForAll(path => _IsUnder(path, folder)))
        {
            folder = _Parent(folder);
        }

        var verb = written.Values.All(created => created) ? "created" : written.Values.Any(created => created) ? "created or changed" : "changed";
        var noun = paths.Count == 1 ? "file" : "files";
        var files = paths
            .Select(path => new TurnResultFile(Path.GetFileName(path), PlainStep.LabelOf(_Parent(path) ?? string.Empty, workingDirectory), path))
            .ToList();
        return new($"Done · {paths.Count} {noun} {verb}", files, folder ?? workingDirectory ?? string.Empty);
    }

    // A bare file name has no folder of its own, which `Path.GetDirectoryName` reports as an empty string.
    private static string? _Parent(string path) => Path.GetDirectoryName(path) is { Length: > 0 } parent ? parent : null;

    private static bool _IsUnder(string path, string folder)
    {
        var relative = Path.GetRelativePath(folder, path);
        return !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
}

public sealed record TurnResultFile(string Name, string? FolderLabel, string Path);

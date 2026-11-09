using System.Text.Json;

namespace Cockpit.Plugin.LocalFiles.Contracts;

// Settings go through the backend, so every root is checked where the plugin runs.
public static class LocalFilesChannel
{
    public const string Roots = "local-files-roots";
    public const string Check = "local-files-check";
    public const string Save = "local-files-save";
    public const string Folders = "local-files-folders";

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web);
}

public sealed record LocalRoot(string Id, string Path, bool Writable, bool Project = false);

// Machine tells the view whether the native picker sees this disk; Hidden names the state folder inside a root.
public sealed record LocalRootsAnswer(IReadOnlyList<LocalRoot> Roots, IReadOnlyDictionary<string, string> Hidden, string Machine,
    string? Error = null);

public sealed record LocalPathRequest(string? Path);

public sealed record LocalRootCheck(string? Path, string? Hidden, string? Error);

public sealed record LocalSaveRequest(IReadOnlyList<LocalRoot> Roots);

public sealed record LocalFolders(string? Path, IReadOnlyList<string> Children, string? Error);

using System.Text.Json;

namespace Cockpit.Plugin.LocalFiles.Contracts;

// The settings view's actions on the plugin's own channel; linked into the UI part, so the backend validates every root.
public static class LocalFilesChannel
{
    public const string Roots = "local-files-roots";
    public const string Check = "local-files-check";
    public const string Save = "local-files-save";
    public const string Folders = "local-files-folders";

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web);
}

public sealed record LocalRoot(string Id, string Path, bool Writable, bool Project = false);

// Machine lets the view tell its own disk from a server's, so it only offers the native picker for its own.
// Hidden maps a root id to the state folder inside it, which the source never shows or opens.
public sealed record LocalRootsAnswer(IReadOnlyList<LocalRoot> Roots, IReadOnlyDictionary<string, string> Hidden, string Machine,
    string? Error = null);

public sealed record LocalPathRequest(string? Path);

public sealed record LocalRootCheck(string? Path, string? Hidden, string? Error);

public sealed record LocalSaveRequest(IReadOnlyList<LocalRoot> Roots);

public sealed record LocalFolders(string? Path, IReadOnlyList<string> Children, string? Error);

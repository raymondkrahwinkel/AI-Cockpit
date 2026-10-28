using System.Text.Json;
using Cockpit.Plugins.Abstractions.Files;

namespace Cockpit.Plugin.Depot.Contracts;

internal static class DepotFileRefs
{
    public static FileRef Create(string connectionId, string project, string path) =>
        new(connectionId, JsonSerializer.Serialize(new[] { project, path }), path);

    public static (string Project, string Path) Parse(FileRef file, string connectionId)
    {
        try
        {
            var id = JsonSerializer.Deserialize<string[]>(file.Id);
            if (file.ConnectionId != connectionId || id is not { Length: 2 }
                || string.IsNullOrWhiteSpace(id[0]) || id[0].Split('/').Length != 2
                || string.IsNullOrWhiteSpace(id[1]) || id[1] != file.Path
                || id.Any(part => string.IsNullOrWhiteSpace(part) || part.Contains('\\') || part.Split('/').Any(s => s is "" or "." or "..")))
                throw new JsonException();
            return (id[0], id[1]);
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            throw new FileSourceException(FileSourceErrorKind.Unsupported, "The Depot file reference is invalid.");
        }
    }
}

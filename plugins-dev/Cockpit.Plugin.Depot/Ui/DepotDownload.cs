namespace Cockpit.Plugin.Depot.UI;

// AC-1568: the file name a download is offered under, and the streaming copy of an artifact's bytes.
internal static class DepotDownload
{
    private static readonly HashSet<string> _Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    // The last path component and nothing else: `a/../../x.png` is `x.png`, never a path that leaves the chosen folder.
    public static string SafeFileName(string depotPath)
    {
        var name = depotPath[(depotPath.LastIndexOfAny(['/', '\\']) + 1)..];
        name = string.Concat(name.Select(character => Array.IndexOf(Path.GetInvalidFileNameChars(), character) >= 0 ? '_' : character)).TrimEnd('.', ' ');
        if (name.Length == 0 || name == "." || name == "..")
        {
            return "download";
        }

        return _Reserved.Contains(Path.GetFileNameWithoutExtension(name)) ? "_" + name : name;
    }

    public static async Task CopyAsync(HttpResponseMessage response, Stream destination, CancellationToken cancellationToken)
    {
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }
}

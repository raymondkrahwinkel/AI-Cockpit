namespace Cockpit.Core.Sessions;

public static class SessionTempDirectory
{
    public static string Root => Path.Combine(Path.GetTempPath(), "cockpit");

    public static string Create(string paneId)
    {
        if (string.IsNullOrWhiteSpace(paneId) || paneId is "." or ".."
            || paneId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || paneId.Contains('/') || paneId.Contains('\\'))
        {
            throw new ArgumentException("A pane id must be a single directory name.", nameof(paneId));
        }

        var path = Path.Combine(Root, paneId);
        Directory.CreateDirectory(path);
        return path;
    }
}

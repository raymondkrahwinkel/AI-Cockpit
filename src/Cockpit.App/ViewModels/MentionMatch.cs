namespace Cockpit.App.ViewModels;

// One row in the @-mention picker. `path` is always '/'-separated and relative to the working directory that
// produced it, with a trailing '/' marking a directory — the same convention the picker inserts into the prompt.
public sealed record MentionMatch(string Path)
{
    public bool IsDirectory => Path.EndsWith('/');

    public string FileName
    {
        get
        {
            var trimmed = IsDirectory ? Path[..^1] : Path;
            var slash = trimmed.LastIndexOf('/');
            return slash < 0 ? trimmed : trimmed[(slash + 1)..];
        }
    }

    public string ParentDirectory
    {
        get
        {
            var trimmed = IsDirectory ? Path[..^1] : Path;
            var slash = trimmed.LastIndexOf('/');
            return slash < 0 ? string.Empty : trimmed[..slash];
        }
    }
}

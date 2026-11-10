namespace Cockpit.Core.Sessions;

public static class GuardrailPaths
{
    public static string Expand(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var value = path.Trim();
        if (value == "~" || value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith("~\\", StringComparison.Ordinal))
        {
            value = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), value.Length == 1 ? string.Empty : value[2..]);
        }

        if (!Path.IsPathFullyQualified(value) || value.Contains(Path.PathSeparator))
        {
            throw new ArgumentException("Allowed paths must be absolute or start with ~/ and cannot contain the path-list separator.", nameof(path));
        }

        return Path.GetFullPath(value);
    }

    public static bool IsValid(string path)
    {
        try
        {
            _ = Expand(path);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }
}

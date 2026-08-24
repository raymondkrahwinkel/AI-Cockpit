namespace Cockpit.App.Services;

// AC-763/AC-1054: `IProjectLogoStore.SaveAsync` only reads a local path or URL, never raw bytes — every caller
// that receives a downloaded logo as bytes (a fresh bind, a later sync pickup) needs this same bridge.
// ponytail: the temp file is never deleted — a few KB in the OS temp folder, not a growing leak.
public static class TempLogoFile
{
    public static string? WriteOrNull(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 })
        {
            return null;
        }

        var path = Path.Combine(Path.GetTempPath(), $"cockpit-shared-logo-{Guid.NewGuid():n}.png");
        File.WriteAllBytes(path, bytes);
        return path;
    }
}

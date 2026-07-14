namespace Cockpit.Infrastructure.Configuration;

/// <summary>
/// Resolves where the cockpit keeps its state (<c>%APPDATA%\Cockpit</c> on Windows, <c>~/.config/Cockpit</c>
/// elsewhere) and owns the file permissions that go with it.
/// <para>
/// The permissions live here rather than at each call site because the files hold credentials — API keys, MCP
/// bearer headers, plugin tokens — and a default <c>File.Create</c> leaves them at whatever the umask says,
/// which is world-readable on a stock Fedora. Every writer of a credential-bearing file goes through
/// <see cref="WriteAllTextPrivate"/> or <see cref="CreatePrivateFile"/>, so "who may read this" is one
/// decision in one place instead of one that each new writer has to remember.
/// </para>
/// </summary>
internal static class CockpitConfigPath
{
    /// <summary>Owner read/write. No group, no other — these files hold credentials.</summary>
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>Owner read/write/traverse. Without the execute bit the owner cannot enter their own directory.</summary>
    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Cockpit");

    public static string Default => Path.Combine(Root, "cockpit.json");

    /// <summary>The plugins root — a <c>plugins/</c> folder next to <c>cockpit.json</c>, stable across app updates. Each plugin lives in its own subfolder here.</summary>
    public static string PluginsRoot => Path.Combine(Root, "plugins");

    /// <summary>Creates <paramref name="directory"/> if needed and restricts it to its owner. Idempotent.</summary>
    public static void EnsurePrivateDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        Restrict(directory, PrivateDirectoryMode);
    }

    /// <summary>
    /// Opens <paramref name="path"/> for writing, truncating it, created owner-only. The mode is set as part of
    /// the create call, so there is no window in which the file exists at the umask's permissions with content
    /// already in it — and an <em>existing</em> file (one written by a version of the cockpit that did not do
    /// this) is restricted on the way past, which is what migrates the operator's current world-readable config.
    /// </summary>
    public static FileStream CreatePrivateFile(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            EnsurePrivateDirectory(directory);
        }

        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
        };

        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = PrivateFileMode;
        }

        var stream = new FileStream(path, options);
        Restrict(path, PrivateFileMode);

        return stream;
    }

    /// <summary>
    /// Restricts a file the cockpit wrote before it knew better. This is what migrates an operator's existing
    /// world-readable <c>cockpit.json</c> without asking them to run <c>chmod</c> on our behalf.
    /// </summary>
    public static void RestrictExistingFile(string path)
    {
        if (File.Exists(path))
        {
            Restrict(path, PrivateFileMode);
        }
    }

    /// <summary>Writes <paramref name="contents"/> to an owner-only file. See <see cref="CreatePrivateFile"/>.</summary>
    public static void WriteAllTextPrivate(string path, string contents)
    {
        using var stream = CreatePrivateFile(path);
        using var writer = new StreamWriter(stream);
        writer.Write(contents);
    }

    /// <summary>
    /// Applies <paramref name="mode"/> to an existing file or directory. A no-op on Windows, which has no Unix
    /// mode bits — there the equivalent protection is the per-user profile directory itself, and pretending
    /// otherwise by throwing would break the platform that does not need this.
    /// </summary>
    private static void Restrict(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, mode);
        }
        catch (Exception)
        {
            // A file on a filesystem that carries no permissions (a mounted share, a container volume) is not a
            // reason to refuse to save the operator's settings. The write itself is what matters most.
        }
    }
}

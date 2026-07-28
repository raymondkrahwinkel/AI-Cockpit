namespace Cockpit.TestSupport;

/// <summary>
/// Where the repository this test belongs to sits on disk. Tests that read the source tree rather than the built
/// assemblies — the theme lints, the theme baselines — need it, and a test binary only knows its own output folder.
/// </summary>
public static class RepositoryPaths
{
    /// <summary>
    /// The first folder above the test output that holds both <c>src/</c> and <c>plugins-dev/</c>. Both, because
    /// either alone occurs elsewhere: a plugin's own output path contains neither, and a partial checkout would
    /// otherwise be mistaken for the root and silently read nothing.
    /// </summary>
    public static string Root { get; } = _Locate();

    private static string _Locate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src"))
                && Directory.Exists(Path.Combine(directory.FullName, "plugins-dev")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "No folder above the test output holds both src/ and plugins-dev/ — this test reads the repo it belongs to.");
    }
}

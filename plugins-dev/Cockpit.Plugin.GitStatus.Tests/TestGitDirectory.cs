namespace Cockpit.Plugin.GitStatus.Tests;

/// <summary>
/// Removes a temp directory a fixture ran <c>git</c> inside. Git writes its loose objects and pack files
/// read-only, and on Windows that attribute makes <see cref="Directory.Delete(string, bool)"/> refuse the file —
/// so a fixture that cleans up the ordinary way throws from its own teardown and fails every test in the class,
/// whatever the assertions said (AC-339, and again here in AC-400).
/// </summary>
/// <remarks>
/// Linux and macOS never showed it: there, unlinking is governed by write permission on the containing directory,
/// not by the mode of the file itself. CI is Linux, so the suite was green there the whole time it was unusable
/// on a Windows desktop.
/// <para>
/// Deliberately a second copy of <c>Cockpit.Infrastructure.Tests.TestGitDirectory</c>, same name so that whoever
/// finds one finds the other: the plugin test projects are built and published independently of the host and hold
/// no reference to its test assemblies. Sharing the file is what is not available here, not what was overlooked.
/// </para>
/// </remarks>
internal static class TestGitDirectory
{
    /// <summary>Deletes the tree if it is there, clearing the read-only attribute git left behind first.</summary>
    internal static void Remove(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                var attributes = File.GetAttributes(file);
                if (attributes.HasFlag(FileAttributes.ReadOnly))
                {
                    // Only the one bit: a blanket FileAttributes.Normal would drop whatever else the file carries.
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }
            }
            catch (Exception)
            {
                // Swallowed on purpose, and it hides nothing: the delete below is still the gate. A virus scanner
                // holding one file for a moment would otherwise throw here and fail every test in the calling class
                // from its Dispose — the exact failure this helper exists to stop, reintroduced one file at a time.
                // If the attribute genuinely could not be cleared, the delete says so, with the path in the message.
            }
        }

        Directory.Delete(path, recursive: true);
    }
}

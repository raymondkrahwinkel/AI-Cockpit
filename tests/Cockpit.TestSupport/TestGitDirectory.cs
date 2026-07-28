namespace Cockpit.TestSupport;

/// <summary>
/// Removes a temp directory a fixture ran <c>git</c> inside. Git writes its loose objects and pack files
/// read-only, and on Windows that attribute makes <see cref="Directory.Delete(string, bool)"/> refuse the file —
/// so a fixture that cleans up the ordinary way throws from its own teardown and fails every test in the class,
/// whatever the assertions said (AC-339, and again in a plugin fixture in AC-400).
/// </summary>
/// <remarks>
/// Linux and macOS never showed it: there, unlinking is governed by write permission on the containing directory,
/// not by the mode of the file itself. CI is Linux, so the suite was green there the whole time it was unusable
/// on a Windows desktop — which is the reason this lives in one place instead of being fixed where it hurt.
/// </remarks>
public static class TestGitDirectory
{
    /// <summary>Deletes the tree if it is there, clearing the read-only attribute git left behind first.</summary>
    public static void Remove(string path)
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
                // If the attribute genuinely could not be cleared, the delete still fails and says which file —
                // though only its name: .NET's recursive delete reports the bare entry, not the path to it, which
                // for a loose git object is a bare sha fragment.
            }
        }

        Directory.Delete(path, recursive: true);
    }
}

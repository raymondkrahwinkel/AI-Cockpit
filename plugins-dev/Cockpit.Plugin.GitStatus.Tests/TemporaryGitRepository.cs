namespace Cockpit.Plugin.GitStatus.Tests;

/// <summary>
/// Removing a scratch repository a test built. Git writes its objects and packs read-only; on Windows a read-only
/// file cannot be deleted, so <see cref="Directory.Delete(string, bool)"/> throws out of the teardown and a whole
/// fixture of passing tests reports as failures. Shared because both git fixtures here hit it — one of them only
/// once it grows a commit, since a repository with no objects has nothing read-only in it yet.
/// </summary>
internal static class TemporaryGitRepository
{
    public static void Delete(string repository)
    {
        if (!Directory.Exists(repository))
        {
            return;
        }

        // Unix reaches the same end by a different route: the attribute is real there too (objects are 0444), so
        // this issues a chmod, but deletion would have succeeded regardless — it is the directory's own write
        // permission that decides.
        foreach (var file in Directory.EnumerateFiles(repository, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(file);
            if (attributes.HasFlag(FileAttributes.ReadOnly))
            {
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }
        }

        Directory.Delete(repository, recursive: true);
    }
}

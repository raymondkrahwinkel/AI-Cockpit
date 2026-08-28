namespace Cockpit.Infrastructure.Configuration;

// AC-1150: the two `CockpitConfigPath` operations a producer outside this assembly needs for a secret-bearing
// file of its own (AdaptiveGcCompactor's heap dump) — narrower than an InternalsVisibleTo grant, which would
// open every internal here rather than just this mechanism.
public static class OwnerOnlyStorage
{
    public static void EnsurePrivateDirectory(string directory) => CockpitConfigPath.EnsurePrivateDirectory(directory);

    public static void RestrictExistingFile(string path) => CockpitConfigPath.RestrictExistingFile(path);
}

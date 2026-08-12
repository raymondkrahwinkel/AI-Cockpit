using Cockpit.Core.Configuration;

namespace Cockpit.Core.Updates;

// AC-738: the operator's "install on next start", written down for the next launch to find. On disk rather than in
// configuration because `VelopackApp.SetAutoApplyOnStartup` — the only hook that applies a staged package at launch —
// is decided before any locator or config exists.
public static class UpdateOnNextStart
{
    private const string MarkerFileName = "apply-update-on-next-start";

    // Records the request. Returns whether it was written: a caller that promises the operator an update on their
    // next launch has to know that the promise was actually kept somewhere.
    public static bool Request() => Request(CockpitBuild.StateRoot);

    // Whether this launch should apply a staged update. The request is cleared as it is read, so a package that
    // cannot be applied does not have every later launch try again.
    public static bool TakeRequest() => TakeRequest(CockpitBuild.StateRoot);

    internal static bool Request(string stateRoot)
    {
        try
        {
            Directory.CreateDirectory(stateRoot);
            File.WriteAllText(Path.Combine(stateRoot, MarkerFileName), string.Empty);

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool TakeRequest(string stateRoot)
    {
        var marker = Path.Combine(stateRoot, MarkerFileName);

        if (!File.Exists(marker))
        {
            return false;
        }

        try
        {
            File.Delete(marker);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A marker that will not go away is harmless: the launch after this one finds no newer package to apply.
        }

        return true;
    }
}

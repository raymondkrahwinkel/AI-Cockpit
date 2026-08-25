using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Updates;
using Cockpit.Core.Updates;
using Velopack;
using Velopack.Locators;

namespace Cockpit.Infrastructure.Updates;

// Asks the running process whether it is a copy the updater installed (AC-385), through Velopack's locator
// directly rather than an `UpdateManager` (which needs a feed, irrelevant here). A host that never ran
// `VelopackApp.Build().Run()` has no locator; that ordinary state is asked for first, not caught as an exception.
internal sealed class VelopackUpdateSupportProbe : IUpdateSupportProbe, ISingletonService
{
    public UpdateSupport Detect() => Detect(IsInstalledCopy);

    // Both readings taken from the process, so the rule below can be asked without one.
    internal static bool IsInstalledCopy() =>
        IsInstalledCopy(VelopackLocator.IsCurrentSet, static () => VelopackLocator.Current.CurrentlyInstalledVersion);

    // The rule, with both readings handed in. Velopack's locator is a process-wide singleton with no public way
    // to stand one up, so a test cannot otherwise reach the branch that tells an installed copy from an
    // uninstalled one — split out this far so the overload above is left with no decision in it.
    internal static bool IsInstalledCopy(bool locatorIsSet, Func<SemanticVersion?> installedVersion) =>
        locatorIsSet && installedVersion() is not null;

    // The decision, with the reading handed in so a test can supply one that fails. The `catch` is a belt, not
    // the mechanism (both states above are handled without it) — it guards against reading the installation on
    // disk throwing inside a binding, the AC-379 shape where an offer nobody could see was not an offer.
    internal static UpdateSupport Detect(Func<bool> isInstalled)
    {
        try
        {
            return isInstalled() ? UpdateSupport.Supported : UpdateSupport.NotPackaged;
        }
        catch (Exception)
        {
            return UpdateSupport.NotPackaged;
        }
    }
}

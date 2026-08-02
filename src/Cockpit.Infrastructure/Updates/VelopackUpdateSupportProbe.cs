using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Updates;
using Cockpit.Core.Updates;
using Velopack;
using Velopack.Locators;

namespace Cockpit.Infrastructure.Updates;

// Asks the running process whether it is a copy the updater installed (AC-385).
//
// The question is answered by Velopack's locator, which is how the library itself answers it: an installed copy has
// a version recorded beside it, an unpacked tarball or a checkout does not. Asked directly rather than through an
// `UpdateManager`, because a manager needs a feed address and this question has nothing to do with a feed.
//
// There are two ways to be unpackaged and only one of them involves an installation: a host that never ran
// `VelopackApp.Build().Run()` — the test suite, anything hosting this assembly without being the cockpit — has
// no locator at all. `VelopackLocator.Current` throws when unset, so that is asked first: an ordinary,
// expected state should not be reached through an exception.
internal sealed class VelopackUpdateSupportProbe : IUpdateSupportProbe, ISingletonService
{
    public UpdateSupport Detect() => Detect(IsInstalledCopy);

    // Both readings taken from the process, so the rule below can be asked without one.
    internal static bool IsInstalledCopy() =>
        IsInstalledCopy(VelopackLocator.IsCurrentSet, static () => VelopackLocator.Current.CurrentlyInstalledVersion);

    // The rule, with both readings handed in. Velopack's locator is a process-wide singleton with no public way to
    // stand one up, so a test cannot otherwise reach the branch where one exists — and that branch holds the only
    // line that tells an installed copy from an uninstalled one. Split out this far, the overload above is left with
    // no decision in it.
    internal static bool IsInstalledCopy(bool locatorIsSet, Func<SemanticVersion?> installedVersion) =>
        locatorIsSet && installedVersion() is not null;

    // The decision, with the reading handed in so a test can supply one that fails.
    //
    // The `catch` is a belt, not the mechanism — both states above are handled without it. It is here because
    // establishing them reads the installation on disk, and a property that throws inside a binding fails silently
    // and leaves the control at its default visibility: the AC-379 shape, where an offer nobody could see was not
    // an offer. Anything this cannot establish is `UpdateSupport.NotPackaged`, the answer that offers
    // less rather than more.
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

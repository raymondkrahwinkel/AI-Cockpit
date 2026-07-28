using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Updates;
using Cockpit.Core.Updates;
using Velopack.Locators;

namespace Cockpit.Infrastructure.Updates;

/// <summary>
/// Asks the running process whether it is a copy the updater installed (AC-385).
/// <para>
/// The question is answered by Velopack's locator, which is how the library itself answers it: an installed copy has
/// a version recorded beside it, an unpacked tarball or a checkout does not. Asked directly rather than through an
/// <c>UpdateManager</c>, because a manager needs a feed address and this question has nothing to do with a feed.
/// </para>
/// <para>
/// There are two ways to be unpackaged and only one of them involves an installation. The locator is a process-wide
/// singleton that <c>VelopackApp.Build().Run()</c> puts in place, so a host that never ran that bootstrap — the test
/// suite, anything hosting this assembly without being the cockpit — has no locator at all. That is asked first,
/// because <c>VelopackLocator.Current</c> throws when it is unset, and an ordinary, expected state should not be
/// reached through an exception.
/// </para>
/// </summary>
internal sealed class VelopackUpdateSupportProbe : IUpdateSupportProbe, ISingletonService
{
    public UpdateSupport Detect() => Detect(IsInstalledCopy);

    /// <summary>
    /// The reading itself, separate from the decision so a test can establish that it <em>answers</em> in a host
    /// without a locator rather than throwing there. Both spellings return the same
    /// <see cref="UpdateSupport.NotPackaged"/> once the <c>catch</c> below has done its work, so the guard is only
    /// visible from here — and a guard nothing can see is a guard nobody keeps.
    /// </summary>
    internal static bool IsInstalledCopy() =>
        VelopackLocator.IsCurrentSet && VelopackLocator.Current.CurrentlyInstalledVersion is not null;

    /// <summary>
    /// The decision, with the reading of the environment handed in so a test can supply one that fails.
    /// <para>
    /// The <c>catch</c> is a belt, not the mechanism: the two states this is actually asking about are both handled
    /// above without one. It is here because establishing them reads the installation on disk, and there is no
    /// version of "I could not work out what this copy is" that should reach a binding as an exception — a property
    /// that throws inside one fails silently and leaves the control at its default visibility, which is the failure
    /// shape AC-379 was: an offer nobody could see is not an offer. Anything this cannot establish is
    /// <see cref="UpdateSupport.NotPackaged"/> — the answer that offers less, never more.
    /// </para>
    /// </summary>
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

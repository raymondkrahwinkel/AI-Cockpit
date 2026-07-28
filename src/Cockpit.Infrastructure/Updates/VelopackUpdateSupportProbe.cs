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
/// </summary>
internal sealed class VelopackUpdateSupportProbe : IUpdateSupportProbe, ISingletonService
{
    public UpdateSupport Detect() => Detect(static () => VelopackLocator.Current.CurrentlyInstalledVersion is not null);

    /// <summary>
    /// The decision, with the reading of the environment handed in so a test can supply one that fails.
    /// <para>
    /// The <c>catch</c> is not decoration. Velopack 1.2.0's locator does not throw on a plain desktop or a test host
    /// — verified, and there is a test that pins it — but it is reading the environment the process was started in,
    /// and there is no version of "I could not work out what this copy is" that should reach a binding as an
    /// exception. A property that throws inside one fails silently and leaves the control at its default
    /// visibility, which is the failure shape AC-379 was: an offer nobody could see is not an offer. Anything this
    /// cannot establish is <see cref="UpdateSupport.NotPackaged"/> — the answer that offers less, never more.
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

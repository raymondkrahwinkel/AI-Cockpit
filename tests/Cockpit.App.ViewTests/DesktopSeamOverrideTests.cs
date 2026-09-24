using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.Core;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Infrastructure.Hosting;

namespace Cockpit.App.ViewTests;

// AC-1381: the backend registers a no-frontend answer for every seam the desktop fills, and the desktop's scan comes
// after it, as `Program` adds it. Red when a desktop seam resolves to the backend's default instead of its own.
public class DesktopSeamOverrideTests
{
    [Theory]
    [InlineData(typeof(IUiHitchProbe))]
    [InlineData(typeof(IDesktopDisplays))]
    [InlineData(typeof(IExternalLinkOpener))]
    public async Task TheDesktopsOwnSeam_ReplacesTheBackendsDefault(Type seam)
    {
        await using var services = CockpitBackend.Build(NullLoggerFactory.Instance, desktop => desktop.AddServices(typeof(App).Assembly)).Services;

        Assert.Equal(typeof(App).Assembly, services.GetRequiredService(seam).GetType().Assembly);
    }
}

using Cockpit.App.ViewModels;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// Guards the split that keeps the running-session panel honest: bypass is a launch-only mode, so it
/// must appear in <see cref="SessionOptionCatalog.AllPermissionModes"/> (the dialog) but never in
/// <see cref="SessionOptionCatalog.LivePermissionModes"/> (the panel dropdown) — no dead control (#15).
/// </summary>
public class SessionOptionCatalogTests
{
    [Fact]
    public void LivePermissionModes_ExcludeBypass_SoItIsNeverALiveSwitch()
    {
        Assert.Equal(
            new[] { "default", "acceptEdits", "plan" },
            SessionOptionCatalog.LivePermissionModes.Select(mode => mode.Value));
    }
}

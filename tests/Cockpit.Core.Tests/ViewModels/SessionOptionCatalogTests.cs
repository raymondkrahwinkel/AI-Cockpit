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
    public void AllPermissionModes_ContainsTheFourRealCliModes()
    {
        Assert.Equal(
            new[] { "default", "acceptEdits", "plan", "bypassPermissions" },
            SessionOptionCatalog.AllPermissionModes.Select(mode => mode.Value));
    }

    [Fact]
    public void LivePermissionModes_ExcludeBypass_SoItIsNeverALiveSwitch()
    {
        Assert.Equal(
            new[] { "default", "acceptEdits", "plan" },
            SessionOptionCatalog.LivePermissionModes.Select(mode => mode.Value));
    }

    [Fact]
    public void ResolvePermissionMode_UnknownValue_FallsBackToTheAppDefault()
    {
        Assert.Equal(SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.ResolvePermissionMode("nonsense"));
        Assert.Equal(SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.ResolvePermissionMode(null));
    }

    [Fact]
    public void ResolvePermissionMode_KnownValue_ReturnsThatOption()
    {
        Assert.Equal("bypassPermissions", SessionOptionCatalog.ResolvePermissionMode("bypassPermissions").Value);
    }

    [Fact]
    public void ResolveModelAndEffort_UnknownValues_FallBackToTheAppDefaults()
    {
        Assert.Equal(SessionOptionCatalog.DefaultModel, SessionOptionCatalog.ResolveModel("nope"));
        Assert.Equal(SessionOptionCatalog.DefaultEffort, SessionOptionCatalog.ResolveEffort("nope"));
    }
}

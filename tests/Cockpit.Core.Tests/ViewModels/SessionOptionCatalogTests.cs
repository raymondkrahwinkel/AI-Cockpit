using Cockpit.App.ViewModels;
using FluentAssertions;

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
        SessionOptionCatalog.AllPermissionModes.Select(mode => mode.Value).Should().Equal(
            "default", "acceptEdits", "plan", "bypassPermissions");
    }

    [Fact]
    public void LivePermissionModes_ExcludeBypass_SoItIsNeverALiveSwitch()
    {
        SessionOptionCatalog.LivePermissionModes.Select(mode => mode.Value).Should().Equal(
            "default", "acceptEdits", "plan");
    }

    [Fact]
    public void ResolvePermissionMode_UnknownValue_FallsBackToTheAppDefault()
    {
        SessionOptionCatalog.ResolvePermissionMode("nonsense").Should().Be(SessionOptionCatalog.DefaultPermissionMode);
        SessionOptionCatalog.ResolvePermissionMode(null).Should().Be(SessionOptionCatalog.DefaultPermissionMode);
    }

    [Fact]
    public void ResolvePermissionMode_KnownValue_ReturnsThatOption()
    {
        SessionOptionCatalog.ResolvePermissionMode("bypassPermissions").Value.Should().Be("bypassPermissions");
    }

    [Fact]
    public void ResolveModelAndEffort_UnknownValues_FallBackToTheAppDefaults()
    {
        SessionOptionCatalog.ResolveModel("nope").Should().Be(SessionOptionCatalog.DefaultModel);
        SessionOptionCatalog.ResolveEffort("nope").Should().Be(SessionOptionCatalog.DefaultEffort);
    }
}

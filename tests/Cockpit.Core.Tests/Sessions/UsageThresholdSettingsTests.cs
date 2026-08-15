using Cockpit.Core.Sessions;

namespace Cockpit.Core.Tests.Sessions;

/// <summary>
/// Where a usage threshold comes from (AC-233): the profile if it said something, else the provider, else what
/// the provider itself declared. One rule, so the pill, the bar and the warning cannot judge the same figure by
/// different numbers — the defect AC-219 was, in another coat.
/// </summary>
public class UsageThresholdSettingsTests
{
    [Fact]
    public void WithNothingSet_TheProvidersOwnDeclarationStands()
    {
        var settings = new UsageThresholdSettings();

        Assert.Equal(90, settings.Resolve("claude", "personal", "weekly", declared: 90, isAssistant: false));
    }

    [Fact]
    public void AProviderOverride_BeatsTheDeclaration()
    {
        var settings = new UsageThresholdSettings();
        settings.Set(settings.ByProvider, "claude", "weekly", 75);

        Assert.Equal(75, settings.Resolve("claude", "personal", "weekly", declared: 90, isAssistant: false));
    }

    [Fact]
    public void AProfileOverride_BeatsTheProvider()
    {
        // A profile used for long unattended runs can be stricter without changing it for everything else.
        var settings = new UsageThresholdSettings();
        settings.Set(settings.ByProvider, "claude", "weekly", 75);
        settings.Set(settings.ByProfile, "work", "weekly", 60);

        Assert.Equal(60, settings.Resolve("claude", "work", "weekly", declared: 90, isAssistant: false));
        Assert.Equal(75, settings.Resolve("claude", "personal", "weekly", declared: 90, isAssistant: false));
    }

    [Fact]
    public void ClearingAnOverride_FallsBackRatherThanFreezingTheCurrentValue()
    {
        // Storing the absence, not a copy: a later change to the provider's own default has to carry.
        var settings = new UsageThresholdSettings();
        settings.Set(settings.ByProvider, "claude", "weekly", 75);

        settings.Set(settings.ByProvider, "claude", "weekly", null);

        Assert.Equal(90, settings.Resolve("claude", null, "weekly", declared: 90, isAssistant: false));
        Assert.DoesNotContain("claude", settings.ByProvider);
    }

    [Fact]
    public void AnOutOfRangeValue_IsClampedRatherThanStored()
    {
        var settings = new UsageThresholdSettings();

        settings.Set(settings.ByProvider, "claude", "context", 140);

        Assert.Equal(100, settings.Resolve("claude", null, "context", declared: 50, isAssistant: false));
    }

    [Fact]
    public void OneSignalsOverride_LeavesTheOthersFollowing()
    {
        var settings = new UsageThresholdSettings();
        settings.Set(settings.ByProvider, "claude", "context", 40);

        Assert.Equal(40, settings.Resolve("claude", null, "context", declared: 50, isAssistant: false));
        Assert.Equal(90, settings.Resolve("claude", null, "weekly", declared: 90, isAssistant: false));
    }

    [Fact]
    public void AnAssistantOverride_BeatsTheProfileAndTheProvider()
    {
        // AC-805: the override hangs off the Assistant's role rather than its profile — a profile the Assistant
        // shares with ordinary sessions must not carry the lower number along for those sessions too.
        var settings = new UsageThresholdSettings();
        settings.Set(settings.ByProvider, "claude", "context", 90);
        settings.Set(settings.ByProfile, "personal", "context", 75);
        settings.Set(settings.ByAssistant, "claude", "context", 25);

        Assert.Equal(25, settings.Resolve("claude", "personal", "context", declared: 50, isAssistant: true));
        Assert.Equal(75, settings.Resolve("claude", "personal", "context", declared: 50, isAssistant: false));
    }

    [Fact]
    public void WithNoAssistantOverride_TheAssistantFallsBackLikeAnOrdinarySession()
    {
        // Leaving the Assistant level alone is not the same as opting out of the profile/provider levels below it.
        var settings = new UsageThresholdSettings();
        settings.Set(settings.ByProfile, "personal", "context", 60);

        Assert.Equal(60, settings.Resolve("claude", "personal", "context", declared: 50, isAssistant: true));
    }
}

using Cockpit.Core.Sessions;
using FluentAssertions;

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

        settings.Resolve("claude", "personal", "weekly", declared: 90).Should().Be(90);
    }

    [Fact]
    public void AProviderOverride_BeatsTheDeclaration()
    {
        var settings = new UsageThresholdSettings();
        settings.Set(settings.ByProvider, "claude", "weekly", 75);

        settings.Resolve("claude", "personal", "weekly", declared: 90).Should().Be(75);
    }

    [Fact]
    public void AProfileOverride_BeatsTheProvider()
    {
        // A profile used for long unattended runs can be stricter without changing it for everything else.
        var settings = new UsageThresholdSettings();
        settings.Set(settings.ByProvider, "claude", "weekly", 75);
        settings.Set(settings.ByProfile, "work", "weekly", 60);

        settings.Resolve("claude", "work", "weekly", declared: 90).Should().Be(60);
        settings.Resolve("claude", "personal", "weekly", declared: 90).Should().Be(75, "another profile still follows the provider");
    }

    [Fact]
    public void ClearingAnOverride_FallsBackRatherThanFreezingTheCurrentValue()
    {
        // Storing the absence, not a copy: a later change to the provider's own default has to carry.
        var settings = new UsageThresholdSettings();
        settings.Set(settings.ByProvider, "claude", "weekly", 75);

        settings.Set(settings.ByProvider, "claude", "weekly", null);

        settings.Resolve("claude", null, "weekly", declared: 90).Should().Be(90);
        settings.ByProvider.Should().NotContainKey("claude", "an empty group is removed rather than left as clutter");
    }

    [Fact]
    public void AnOutOfRangeValue_IsClampedRatherThanStored()
    {
        var settings = new UsageThresholdSettings();

        settings.Set(settings.ByProvider, "claude", "context", 140);

        settings.Resolve("claude", null, "context", declared: 50).Should().Be(100);
    }

    [Fact]
    public void OneSignalsOverride_LeavesTheOthersFollowing()
    {
        var settings = new UsageThresholdSettings();
        settings.Set(settings.ByProvider, "claude", "context", 40);

        settings.Resolve("claude", null, "context", declared: 50).Should().Be(40);
        settings.Resolve("claude", null, "weekly", declared: 90).Should().Be(90);
    }
}

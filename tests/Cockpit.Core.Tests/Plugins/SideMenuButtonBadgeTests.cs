using Cockpit.Plugins.Abstractions;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// <see cref="SideMenuButtonBadge"/> (AC-516): the plugin-owned counter handle a side-menu launcher renders. Covers
/// the rendering rule acceptance criterion 3 demands — null ("not yet known") and a real zero must read differently
/// — for every combination of the two counters, and that <see cref="SideMenuButtonBadge.Changed"/> fires only on an
/// actual change (never on a no-op re-set of the same value), which is what lets a plugin poll its own source freely
/// without spamming the host's rendering.
/// </summary>
public class SideMenuButtonBadgeTests
{
    [Fact]
    public void BothCountersUnknown_RendersNothing()
    {
        var badge = new SideMenuButtonBadge();

        Assert.Equal(string.Empty, badge.ToDisplayText());
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(3, "3")]
    [InlineData(100, "100")]
    public void OnlyPrimarySet_RendersThatNumber_ZeroIncluded(int primary, string expected)
    {
        var badge = new SideMenuButtonBadge { Primary = primary };

        Assert.Equal(expected, badge.ToDisplayText());
    }

    [Theory]
    [InlineData(3, 2, "3 / 2")]
    [InlineData(0, 0, "0 / 0")]
    [InlineData(100, 100, "100 / 100")]
    public void BothCountersSet_RendersPrimarySlashSecondary(int primary, int secondary, string expected)
    {
        var badge = new SideMenuButtonBadge { Primary = primary, Secondary = secondary };

        Assert.Equal(expected, badge.ToDisplayText());
    }

    // A secondary count means nothing without a primary one beside it (documented on the type) — setting it alone
    // must not make the badge appear to know something it does not.
    [Fact]
    public void SecondarySetWithoutPrimary_StillRendersNothing()
    {
        var badge = new SideMenuButtonBadge { Secondary = 2 };

        Assert.Equal(string.Empty, badge.ToDisplayText());
    }

    [Fact]
    public void SettingPrimary_ToADifferentValue_RaisesChanged()
    {
        var badge = new SideMenuButtonBadge();
        var raised = 0;
        badge.Changed += () => raised++;

        badge.Primary = 3;

        Assert.Equal(1, raised);
    }

    [Fact]
    public void SettingPrimary_ToTheSameValueAgain_DoesNotRaiseChanged()
    {
        var badge = new SideMenuButtonBadge { Primary = 3 };
        var raised = 0;
        badge.Changed += () => raised++;

        badge.Primary = 3;

        Assert.Equal(0, raised);
    }

    [Fact]
    public void SettingSecondary_ToADifferentValue_RaisesChanged()
    {
        var badge = new SideMenuButtonBadge { Primary = 3 };
        var raised = 0;
        badge.Changed += () => raised++;

        badge.Secondary = 2;

        Assert.Equal(1, raised);
    }
}

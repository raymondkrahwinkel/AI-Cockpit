using Cockpit.Core.Plugins;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// Splitting a plugin in two leaves the old one installed — the installer never removes what an operator has,
/// which is right. But its successors keep the widget type ids it registered, so a saved dashboard survives the
/// split and the old plugin ends up claiming the same types as the new ones. Only one of each can win, so the
/// operator has to be told; this is the decision of when. Which plugins count as competing — the enabled ones,
/// not every one with a registration — is <see cref="Cockpit.App.Services.SupersededPluginNotice"/>'s to answer.
/// </summary>
public class SupersededPluginTests
{
    private static readonly SupersededPlugin Widgets = new("widgets", "Reference widgets", ["clock", "system-monitor"]);

    [Fact]
    public void ShouldOffer_WhenTheOldPluginAndASuccessorAreBothEnabled_SaysSo()
    {
        Assert.True(Widgets.ShouldOffer(["widgets", "clock", "git-status"]));
    }

    /// <summary>Without the old one there is nothing to remove — the ordinary case, and it must stay quiet.</summary>
    [Fact]
    public void ShouldOffer_WithOnlyTheSuccessors_SaysNothing()
    {
        Assert.False(Widgets.ShouldOffer(["clock", "system-monitor"]));
    }

    /// <summary>
    /// An old plugin whose successors are not enabled is just a plugin: it is the only thing contributing its
    /// widgets, it works, and offering to remove it would break a working dashboard.
    /// </summary>
    [Fact]
    public void ShouldOffer_WithNoSuccessorEnabled_SaysNothing()
    {
        Assert.False(Widgets.ShouldOffer(["widgets", "git-status"]));
    }

    [Fact]
    public void ShouldOffer_WithNothingEnabled_SaysNothing()
    {
        Assert.False(Widgets.ShouldOffer([]));
    }

    /// <summary>One successor is enough: the clock ships with the app, the monitor comes from the store when wanted.</summary>
    [Fact]
    public void ShouldOffer_WithOnlyTheBundledSuccessor_StillSaysSo()
    {
        Assert.True(Widgets.ShouldOffer(["widgets", "clock"]));
    }

    /// <summary>
    /// The list is a migration aid, not a mechanism to grow. Each entry costs every operator a check on every
    /// start and a sentence they may not need, so an entry has to earn its place.
    /// </summary>
    [Fact]
    public void Known_HoldsTheOneSplitThisBuildMade()
    {
        Assert.Equivalent(new SupersededPlugin("widgets", "Reference widgets", ["clock", "system-monitor"]), Assert.Single(SupersededPlugin.Known));
    }
}

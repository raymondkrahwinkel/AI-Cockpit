using Cockpit.App.ViewModels;
using Cockpit.Core.Profiles;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// AC-661 criterion 4: a session running up against its own memory cap says so on its bar before the operating
/// system cuts it off, and stops saying it once it drops back — the difference between "that pane died for no
/// visible reason" and "you were told, and the cockpit is still here".
/// </summary>
public class SessionMemoryCapWarningTests
{
    [Fact]
    public void ASessionNearItsCap_SaysSoOnTheBar_AndFallsSilentWhenItDropsBack()
    {
        var panel = new TtyViewModel { MemoryCapBytes = 1000 };

        panel.ReportMemoryAgainstCap(500);
        Assert.Empty(panel.UsageWarning);

        panel.ReportMemoryAgainstCap(850);
        Assert.Contains("memory cap", panel.UsageWarning);
        Assert.True(panel.HasUsageWarning);

        panel.ReportMemoryAgainstCap(400);
        Assert.Empty(panel.UsageWarning);
    }

    [Fact]
    public void PastTheCap_TheSameBarChangesItsWords_AndOffersTheKill()
    {
        // AC-700: the second state of one warning, not a second warning — the bar reads differently and grows a
        // Kill button, because since AC-692 nothing closes the session on its own.
        var panel = new TtyViewModel { MemoryCapBytes = 1000 };

        panel.ReportMemoryAgainstCap(850);
        Assert.False(panel.IsOverMemoryCap);

        panel.ReportMemoryAgainstCap(1100);
        Assert.True(panel.IsOverMemoryCap);
        Assert.Contains("over its", panel.UsageWarning);

        // Hysteresis is `SessionMemoryPressure`'s: back under the cap is not enough, it has to fall well back.
        panel.ReportMemoryAgainstCap(950);
        Assert.True(panel.IsOverMemoryCap);

        panel.ReportMemoryAgainstCap(850);
        Assert.False(panel.IsOverMemoryCap);
        Assert.Contains("of its", panel.UsageWarning);
    }

    [Fact]
    public void ADismissAtTheWarningLine_DoesNotHideTheKillWhenTheCapIsActuallyGone()
    {
        // "Keep an eye on this" was clicked away; the cap being spent is a different message, and the button that
        // acts on it lives inside the bar — silenced, it would sit where nothing can reach it.
        var panel = new TtyViewModel { MemoryCapBytes = 1000 };

        panel.ReportMemoryAgainstCap(850);
        panel.DismissUsageWarningCommand.Execute(null);
        Assert.False(panel.HasUsageWarning);

        panel.ReportMemoryAgainstCap(1100);

        Assert.True(panel.HasUsageWarning);
        Assert.Contains("over its", panel.UsageWarning);
    }

    [Fact]
    public void OverTheCap_ItsLineBlocks_AndSortsAboveAMerelyLimitingWarning()
    {
        // AC-683 criterion 9: what blocks the session (spent, cut off by the OS) sorts above what only limits it
        // (an allowance running low) — regardless of which crossed first.
        var panel = new TtyViewModel { MemoryCapBytes = 1000, UsageProviderId = "claude" };
        var weekly = new PluginUsageSignal("weekly", "wk", PluginUsageSignalKind.Allowance, 90) { Description = "Week" };
        panel.ApplyUsage([weekly], [new PluginUsageReading("weekly", 95, null)]);

        panel.ReportMemoryAgainstCap(1100);

        Assert.Equal(2, panel.Warnings.Count);
        Assert.Contains("over its", panel.Warnings[0].Text);
        Assert.True(panel.Warnings[0].ShowKill);
        Assert.Contains("Week is 95% used", panel.Warnings[1].Text);
        Assert.False(panel.Warnings[1].ShowKill);

        // Dismissing the memory-cap line by its own key must not take the weekly line down with it.
        panel.DismissWarningCommand.Execute("cockpit.memory-cap");
        Assert.Single(panel.Warnings);
        Assert.Contains("Week is 95% used", panel.Warnings[0].Text);
    }

    [Fact]
    public void WithNoCapAtAll_NothingIsClaimed()
    {
        // macOS, where nothing enforces a cap: a bar that warned about a ceiling that does not exist would be
        // worse than silence.
        var panel = new TtyViewModel();

        panel.ReportMemoryAgainstCap(long.MaxValue / 2);

        Assert.Empty(panel.UsageWarning);
    }

    [Fact]
    public void TheEditorRoundTripsTheProfilesCap_AndTreatsABlankAsTheDefault()
    {
        var profile = new SessionProfile("work", ClaudePluginProfile.Create("/home/r/.claude-work", null))
        {
            MemoryCapMegabytes = 4096,
        };

        var editable = new EditableProfileViewModel(profile, isLoggedIn: true);
        Assert.Equal(4096, editable.MemoryCapMegabytes);
        Assert.Equal(4096, editable.ToProfile().MemoryCapMegabytes);

        editable.MemoryCapMegabytes = null;
        Assert.Null(editable.ToProfile().MemoryCapMegabytes);
    }
}

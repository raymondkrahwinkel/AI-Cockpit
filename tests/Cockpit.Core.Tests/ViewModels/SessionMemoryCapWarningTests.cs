using Cockpit.App.ViewModels;
using Cockpit.Core.Profiles;

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
        Assert.Equal("4096", editable.MemoryCapMegabytes);
        Assert.Equal(4096, editable.ToProfile().MemoryCapMegabytes);

        editable.MemoryCapMegabytes = string.Empty;
        Assert.Null(editable.ToProfile().MemoryCapMegabytes);
    }
}

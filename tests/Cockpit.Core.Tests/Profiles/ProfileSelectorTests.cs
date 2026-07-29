using Cockpit.Core.Profiles;

namespace Cockpit.Core.Tests.Profiles;

public class ProfileSelectorTests
{
    [Fact]
    public void Select_NoProfiles_ReturnsLoginRequired()
    {
        var outcome = ProfileSelector.Select([]);

        Assert.Equal(ProfileSelectionKind.LoginRequired, outcome.Kind);
        Assert.Null(outcome.SingleProfile);
        Assert.Empty(outcome.Candidates);
    }

    [Fact]
    public void Select_ProfilesExistButNoneLoggedIn_ReturnsLoginRequired()
    {
        var statuses = new[]
        {
            new SessionProfileStatus(new SessionProfile("default", new ClaudeConfig(@"C:\Users\raymo\.claude")), IsLoggedIn: false),
            new SessionProfileStatus(new SessionProfile("work", new ClaudeConfig(@"C:\Users\raymo\.claude-work")), IsLoggedIn: false),
        };

        var outcome = ProfileSelector.Select(statuses);

        Assert.Equal(ProfileSelectionKind.LoginRequired, outcome.Kind);
    }

    [Fact]
    public void Select_ExactlyOneLoggedIn_ReturnsUseSilentlyWithThatProfile()
    {
        var loggedIn = new SessionProfile("default", new ClaudeConfig(@"C:\Users\raymo\.claude"));
        var statuses = new[]
        {
            new SessionProfileStatus(loggedIn, IsLoggedIn: true),
            new SessionProfileStatus(new SessionProfile("work", new ClaudeConfig(@"C:\Users\raymo\.claude-work")), IsLoggedIn: false),
        };

        var outcome = ProfileSelector.Select(statuses);

        Assert.Equal(ProfileSelectionKind.UseSilently, outcome.Kind);
        Assert.Equal(loggedIn, outcome.SingleProfile);
    }

    [Fact]
    public void Select_MoreThanOneLoggedIn_ReturnsRequiresChoiceWithOnlyLoggedInCandidates()
    {
        var personal = new SessionProfile("personal", new ClaudeConfig(@"C:\Users\raymo\.claude-personal"));
        var work = new SessionProfile("work", new ClaudeConfig(@"C:\Users\raymo\.claude-work"));
        var statuses = new[]
        {
            new SessionProfileStatus(personal, IsLoggedIn: true),
            new SessionProfileStatus(work, IsLoggedIn: true),
            new SessionProfileStatus(new SessionProfile("stale", new ClaudeConfig(@"C:\Users\raymo\.claude-stale")), IsLoggedIn: false),
        };

        var outcome = ProfileSelector.Select(statuses);

        Assert.Equal(ProfileSelectionKind.RequiresChoice, outcome.Kind);
        Assert.Null(outcome.SingleProfile);
        Assert.Equivalent(new object[] { personal, work }, outcome.Candidates);
    }

    [Fact]
    public void Select_MoreThanOneLoggedInWithLastUsed_MovesLastUsedToFrontOfCandidates()
    {
        var personal = new SessionProfile("personal", new ClaudeConfig(@"C:\Users\raymo\.claude-personal"));
        var work = new SessionProfile("work", new ClaudeConfig(@"C:\Users\raymo\.claude-work"));
        var statuses = new[]
        {
            new SessionProfileStatus(personal, IsLoggedIn: true),
            new SessionProfileStatus(work, IsLoggedIn: true),
        };

        var outcome = ProfileSelector.Select(statuses, lastUsedLabel: "work");

        Assert.Equal(ProfileSelectionKind.RequiresChoice, outcome.Kind);
        Assert.Equal(new[] { work, personal }, outcome.Candidates);
    }

    [Fact]
    public void Select_LastUsedLabelUnknown_LeavesCandidateOrderUnchanged()
    {
        var personal = new SessionProfile("personal", new ClaudeConfig(@"C:\Users\raymo\.claude-personal"));
        var work = new SessionProfile("work", new ClaudeConfig(@"C:\Users\raymo\.claude-work"));
        var statuses = new[]
        {
            new SessionProfileStatus(personal, IsLoggedIn: true),
            new SessionProfileStatus(work, IsLoggedIn: true),
        };

        var outcome = ProfileSelector.Select(statuses, lastUsedLabel: "nonexistent");

        Assert.Equal(new[] { personal, work }, outcome.Candidates);
    }
}

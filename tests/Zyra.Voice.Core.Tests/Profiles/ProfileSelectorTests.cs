using FluentAssertions;
using Zyra.Voice.Core.Profiles;

namespace Zyra.Voice.Core.Tests.Profiles;

public class ProfileSelectorTests
{
    [Fact]
    public void Select_NoProfiles_ReturnsLoginRequired()
    {
        var outcome = ProfileSelector.Select([]);

        outcome.Kind.Should().Be(ProfileSelectionKind.LoginRequired);
        outcome.SingleProfile.Should().BeNull();
        outcome.Candidates.Should().BeEmpty();
    }

    [Fact]
    public void Select_ProfilesExistButNoneLoggedIn_ReturnsLoginRequired()
    {
        var statuses = new[]
        {
            new ClaudeProfileStatus(new ClaudeProfile("default", @"C:\Users\raymo\.claude"), IsLoggedIn: false),
            new ClaudeProfileStatus(new ClaudeProfile("work", @"C:\Users\raymo\.claude-work"), IsLoggedIn: false),
        };

        var outcome = ProfileSelector.Select(statuses);

        outcome.Kind.Should().Be(ProfileSelectionKind.LoginRequired);
    }

    [Fact]
    public void Select_ExactlyOneLoggedIn_ReturnsUseSilentlyWithThatProfile()
    {
        var loggedIn = new ClaudeProfile("default", @"C:\Users\raymo\.claude");
        var statuses = new[]
        {
            new ClaudeProfileStatus(loggedIn, IsLoggedIn: true),
            new ClaudeProfileStatus(new ClaudeProfile("work", @"C:\Users\raymo\.claude-work"), IsLoggedIn: false),
        };

        var outcome = ProfileSelector.Select(statuses);

        outcome.Kind.Should().Be(ProfileSelectionKind.UseSilently);
        outcome.SingleProfile.Should().Be(loggedIn);
    }

    [Fact]
    public void Select_MoreThanOneLoggedIn_ReturnsRequiresChoiceWithOnlyLoggedInCandidates()
    {
        var personal = new ClaudeProfile("personal", @"C:\Users\raymo\.claude-personal");
        var work = new ClaudeProfile("work", @"C:\Users\raymo\.claude-work");
        var statuses = new[]
        {
            new ClaudeProfileStatus(personal, IsLoggedIn: true),
            new ClaudeProfileStatus(work, IsLoggedIn: true),
            new ClaudeProfileStatus(new ClaudeProfile("stale", @"C:\Users\raymo\.claude-stale"), IsLoggedIn: false),
        };

        var outcome = ProfileSelector.Select(statuses);

        outcome.Kind.Should().Be(ProfileSelectionKind.RequiresChoice);
        outcome.SingleProfile.Should().BeNull();
        outcome.Candidates.Should().BeEquivalentTo([personal, work]);
    }

    [Fact]
    public void Select_MoreThanOneLoggedInWithLastUsed_MovesLastUsedToFrontOfCandidates()
    {
        var personal = new ClaudeProfile("personal", @"C:\Users\raymo\.claude-personal");
        var work = new ClaudeProfile("work", @"C:\Users\raymo\.claude-work");
        var statuses = new[]
        {
            new ClaudeProfileStatus(personal, IsLoggedIn: true),
            new ClaudeProfileStatus(work, IsLoggedIn: true),
        };

        var outcome = ProfileSelector.Select(statuses, lastUsedLabel: "work");

        outcome.Kind.Should().Be(ProfileSelectionKind.RequiresChoice);
        outcome.Candidates.Should().ContainInOrder(work, personal);
    }

    [Fact]
    public void Select_LastUsedLabelUnknown_LeavesCandidateOrderUnchanged()
    {
        var personal = new ClaudeProfile("personal", @"C:\Users\raymo\.claude-personal");
        var work = new ClaudeProfile("work", @"C:\Users\raymo\.claude-work");
        var statuses = new[]
        {
            new ClaudeProfileStatus(personal, IsLoggedIn: true),
            new ClaudeProfileStatus(work, IsLoggedIn: true),
        };

        var outcome = ProfileSelector.Select(statuses, lastUsedLabel: "nonexistent");

        outcome.Candidates.Should().ContainInOrder(personal, work);
    }
}

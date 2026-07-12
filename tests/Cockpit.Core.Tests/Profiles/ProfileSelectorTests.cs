using FluentAssertions;
using Cockpit.Core.Profiles;

namespace Cockpit.Core.Tests.Profiles;

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
            new SessionProfileStatus(new SessionProfile("default", @"C:\Users\raymo\.claude"), IsLoggedIn: false),
            new SessionProfileStatus(new SessionProfile("work", @"C:\Users\raymo\.claude-work"), IsLoggedIn: false),
        };

        var outcome = ProfileSelector.Select(statuses);

        outcome.Kind.Should().Be(ProfileSelectionKind.LoginRequired);
    }

    [Fact]
    public void Select_ExactlyOneLoggedIn_ReturnsUseSilentlyWithThatProfile()
    {
        var loggedIn = new SessionProfile("default", @"C:\Users\raymo\.claude");
        var statuses = new[]
        {
            new SessionProfileStatus(loggedIn, IsLoggedIn: true),
            new SessionProfileStatus(new SessionProfile("work", @"C:\Users\raymo\.claude-work"), IsLoggedIn: false),
        };

        var outcome = ProfileSelector.Select(statuses);

        outcome.Kind.Should().Be(ProfileSelectionKind.UseSilently);
        outcome.SingleProfile.Should().Be(loggedIn);
    }

    [Fact]
    public void Select_MoreThanOneLoggedIn_ReturnsRequiresChoiceWithOnlyLoggedInCandidates()
    {
        var personal = new SessionProfile("personal", @"C:\Users\raymo\.claude-personal");
        var work = new SessionProfile("work", @"C:\Users\raymo\.claude-work");
        var statuses = new[]
        {
            new SessionProfileStatus(personal, IsLoggedIn: true),
            new SessionProfileStatus(work, IsLoggedIn: true),
            new SessionProfileStatus(new SessionProfile("stale", @"C:\Users\raymo\.claude-stale"), IsLoggedIn: false),
        };

        var outcome = ProfileSelector.Select(statuses);

        outcome.Kind.Should().Be(ProfileSelectionKind.RequiresChoice);
        outcome.SingleProfile.Should().BeNull();
        outcome.Candidates.Should().BeEquivalentTo([personal, work]);
    }

    [Fact]
    public void Select_MoreThanOneLoggedInWithLastUsed_MovesLastUsedToFrontOfCandidates()
    {
        var personal = new SessionProfile("personal", @"C:\Users\raymo\.claude-personal");
        var work = new SessionProfile("work", @"C:\Users\raymo\.claude-work");
        var statuses = new[]
        {
            new SessionProfileStatus(personal, IsLoggedIn: true),
            new SessionProfileStatus(work, IsLoggedIn: true),
        };

        var outcome = ProfileSelector.Select(statuses, lastUsedLabel: "work");

        outcome.Kind.Should().Be(ProfileSelectionKind.RequiresChoice);
        outcome.Candidates.Should().ContainInOrder(work, personal);
    }

    [Fact]
    public void Select_LastUsedLabelUnknown_LeavesCandidateOrderUnchanged()
    {
        var personal = new SessionProfile("personal", @"C:\Users\raymo\.claude-personal");
        var work = new SessionProfile("work", @"C:\Users\raymo\.claude-work");
        var statuses = new[]
        {
            new SessionProfileStatus(personal, IsLoggedIn: true),
            new SessionProfileStatus(work, IsLoggedIn: true),
        };

        var outcome = ProfileSelector.Select(statuses, lastUsedLabel: "nonexistent");

        outcome.Candidates.Should().ContainInOrder(personal, work);
    }
}

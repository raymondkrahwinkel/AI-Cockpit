using Cockpit.Core.Sessions;
using FluentAssertions;

namespace Cockpit.Core.Tests.Sessions;

/// <summary>
/// What several plugins between them put in one session's environment (AC-165). The rules that matter here are the
/// two an operator would otherwise discover the hard way: which plugin wins a variable both set, and that a plugin
/// cannot set a key the host owns.
/// </summary>
public class SessionResourceMergeTests
{
    private static SessionResources Contribution(params (string Key, string Value)[] variables) =>
        new(variables.ToDictionary(variable => variable.Key, variable => variable.Value, StringComparer.Ordinal));

    [Fact]
    public void Merge_TwoPluginsSettingTheSameVariable_KeepsTheFirst()
    {
        // Last-one-wins would make a session's environment depend on plugin load order, which changes when the
        // operator installs something unrelated.
        var (resources, _) = SessionResourceMerge.Merge(
            [Contribution(("GH_REPO", "raymondkrahwinkel/AI-Cockpit")), Contribution(("GH_REPO", "someone/else"))]);

        resources.EnvironmentVariables.Should().Contain("GH_REPO", "raymondkrahwinkel/AI-Cockpit");
    }

    [Fact]
    public void Merge_AHostControlledKey_IsRefusedAndReportedByName()
    {
        var (resources, rejected) = SessionResourceMerge.Merge(
            [Contribution(("ANTHROPIC_API_KEY", "smuggled"), ("GH_REPO", "owner/repo"))]);

        resources.EnvironmentVariables.Should().NotContainKey("ANTHROPIC_API_KEY");
        resources.EnvironmentVariables.Should().Contain("GH_REPO", "owner/repo", "the rest of the contribution still applies");
        rejected.Should().Equal("ANTHROPIC_API_KEY");
    }

    [Fact]
    public void Merge_ARefusedKey_IsNotWhatMakesTheResultNonEmpty()
    {
        // A contribution consisting only of keys the host owns must leave the session exactly as it was, rather than
        // an empty dictionary that reads as "a plugin contributed something".
        var (resources, rejected) = SessionResourceMerge.Merge([Contribution(("CLAUDECODE", "1"))]);

        resources.IsEmpty.Should().BeTrue();
        rejected.Should().Equal("CLAUDECODE");
    }

    [Fact]
    public void Merge_NoContributions_IsEmpty()
    {
        var (resources, rejected) = SessionResourceMerge.Merge([]);

        resources.Should().BeSameAs(SessionResources.Empty);
        rejected.Should().BeEmpty();
    }

    [Fact]
    public void Merge_KeysDifferingOnlyInCase_AreNotFoldedHere()
    {
        // The merge matches the SDK route's own environment dictionary, which is ordinal. It is not a promise that
        // the session ends up with two variables: the TTY route composes through TtyEnvironment, whose dictionary is
        // case-insensitive, so there one of these would win. What this pins is that the fold is not this layer's
        // doing — so a plugin contributing a lowercase key does not quietly lose it before the routes even see it.
        var (resources, _) = SessionResourceMerge.Merge([Contribution(("gh_repo", "one"), ("GH_REPO", "two"))]);

        resources.EnvironmentVariables.Should().HaveCount(2);
    }
}

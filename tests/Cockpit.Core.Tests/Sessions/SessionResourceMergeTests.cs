using Cockpit.Core.Sessions;

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
    public void Merge_AHostControlledKey_IsRefusedAndReportedByName()
    {
        var (resources, rejected) = SessionResourceMerge.Merge(
            [Contribution(("ANTHROPIC_API_KEY", "smuggled"), ("GH_REPO", "owner/repo"))]);

        Assert.DoesNotContain("ANTHROPIC_API_KEY", resources.EnvironmentVariables);
        Assert.Equal("owner/repo", resources.EnvironmentVariables["GH_REPO"]);
        Assert.Equal(new[] { "ANTHROPIC_API_KEY" }, rejected);
    }

}

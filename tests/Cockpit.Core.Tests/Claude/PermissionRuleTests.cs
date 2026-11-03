using Cockpit.Core.Sessions.Permissions;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// Matching semantics for always-allow rules: exact = same tool + same (canonicalized) input,
/// wildcard = same tool for any input.
/// </summary>
public class PermissionRuleTests
{
    [Fact]
    public void Wildcard_MatchesAnyInputForTheSameTool()
    {
        var rule = PermissionRule.ForWildcard("Bash");

        Assert.True(rule.Matches("Bash", """{"command":"ls"}"""));
        Assert.True(rule.Matches("Bash", """{"command":"rm -rf /"}"""));
    }

    [Fact]
    public void Exact_MatchesRegardlessOfPropertyOrderOrWhitespace()
    {
        var rule = PermissionRule.ForExact("Edit", """{"file_path":"a.txt","old_string":"x"}""");

        Assert.True(rule.Matches("Edit", """{ "old_string": "x",  "file_path": "a.txt" }"""));
    }

    [Fact]
    public void Exact_DoesNotMatchADifferentInput()
    {
        var rule = PermissionRule.ForExact("Bash", """{"command":"dotnet build"}""");

        Assert.False(rule.Matches("Bash", """{"command":"dotnet test"}"""));
    }

}

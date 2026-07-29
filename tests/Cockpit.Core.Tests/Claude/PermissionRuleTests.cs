using System.Text.Json;
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
    public void Wildcard_DoesNotMatchADifferentTool()
    {
        var rule = PermissionRule.ForWildcard("Bash");

        Assert.False(rule.Matches("Edit", "{}"));
    }

    [Fact]
    public void Exact_MatchesTheSameToolAndInput()
    {
        var rule = PermissionRule.ForExact("Bash", """{"command":"dotnet build"}""");

        Assert.True(rule.Matches("Bash", """{"command":"dotnet build"}"""));
    }

    [Fact]
    public void Exact_MatchesRegardlessOfPropertyOrderOrWhitespace()
    {
        var rule = PermissionRule.ForExact("Edit", """{"file_path":"a.txt","old_string":"x"}""");

        Assert.True(rule.Matches("Edit", """{ "old_string": "x",  "file_path": "a.txt" }"""));
    }

    [Theory]
    [InlineData(">")]
    [InlineData("<")]
    [InlineData("&")]
    public void Exact_MatchesWhenSourcesEscapeSpecialCharactersDifferently(string special)
    {
        // The stream tool_use JSON carries '>' / '<' / '&' literally; the MCP permission_prompt JSON
        // emits them as \uXXXX escapes — exactly what System.Text.Json produces here. Both must
        // canonicalize to the same fingerprint, otherwise "Always (exact)" re-prompts forever for
        // most shell commands (bug #27).
        var command = $"echo a {special} b";
        var literalInput = $$"""{"command":"echo a {{special}} b"}""";
        var escapedInput = JsonSerializer.Serialize(new { command });

        var rule = PermissionRule.ForExact("Bash", literalInput);

        Assert.True(rule.Matches("Bash", escapedInput));
    }

    [Fact]
    public void Exact_DoesNotMatchADifferentInput()
    {
        var rule = PermissionRule.ForExact("Bash", """{"command":"dotnet build"}""");

        Assert.False(rule.Matches("Bash", """{"command":"dotnet test"}"""));
    }

    [Fact]
    public void Exact_DoesNotMatchADifferentTool()
    {
        var rule = PermissionRule.ForExact("Bash", """{"command":"dotnet build"}""");

        Assert.False(rule.Matches("Write", """{"command":"dotnet build"}"""));
    }

    [Fact]
    public void RuleSet_Add_IsIdempotentForAnEqualRule()
    {
        var set = new PermissionRuleSet();

        Assert.True(set.Add(PermissionRule.ForWildcard("Bash")));
        Assert.False(set.Add(PermissionRule.ForWildcard("Bash")));
        Assert.Single(set.Snapshot());
    }

    [Fact]
    public void RuleSet_IsAlwaysAllowed_ReflectsItsRules()
    {
        var set = new PermissionRuleSet([PermissionRule.ForExact("Read", """{"file_path":"a.txt"}""")]);

        Assert.True(set.IsAlwaysAllowed("Read", """{"file_path":"a.txt"}"""));
        Assert.False(set.IsAlwaysAllowed("Read", """{"file_path":"b.txt"}"""));
    }
}

using System.Text.Json;
using Cockpit.Core.Claude.Permissions;
using FluentAssertions;

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

        rule.Matches("Bash", """{"command":"ls"}""").Should().BeTrue();
        rule.Matches("Bash", """{"command":"rm -rf /"}""").Should().BeTrue();
    }

    [Fact]
    public void Wildcard_DoesNotMatchADifferentTool()
    {
        var rule = PermissionRule.ForWildcard("Bash");

        rule.Matches("Edit", "{}").Should().BeFalse();
    }

    [Fact]
    public void Exact_MatchesTheSameToolAndInput()
    {
        var rule = PermissionRule.ForExact("Bash", """{"command":"dotnet build"}""");

        rule.Matches("Bash", """{"command":"dotnet build"}""").Should().BeTrue();
    }

    [Fact]
    public void Exact_MatchesRegardlessOfPropertyOrderOrWhitespace()
    {
        var rule = PermissionRule.ForExact("Edit", """{"file_path":"a.txt","old_string":"x"}""");

        rule.Matches("Edit", """{ "old_string": "x",  "file_path": "a.txt" }""").Should().BeTrue();
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

        rule.Matches("Bash", escapedInput).Should().BeTrue();
    }

    [Fact]
    public void Exact_DoesNotMatchADifferentInput()
    {
        var rule = PermissionRule.ForExact("Bash", """{"command":"dotnet build"}""");

        rule.Matches("Bash", """{"command":"dotnet test"}""").Should().BeFalse();
    }

    [Fact]
    public void Exact_DoesNotMatchADifferentTool()
    {
        var rule = PermissionRule.ForExact("Bash", """{"command":"dotnet build"}""");

        rule.Matches("Write", """{"command":"dotnet build"}""").Should().BeFalse();
    }

    [Fact]
    public void RuleSet_Add_IsIdempotentForAnEqualRule()
    {
        var set = new PermissionRuleSet();

        set.Add(PermissionRule.ForWildcard("Bash")).Should().BeTrue();
        set.Add(PermissionRule.ForWildcard("Bash")).Should().BeFalse();
        set.Snapshot().Should().ContainSingle();
    }

    [Fact]
    public void RuleSet_IsAlwaysAllowed_ReflectsItsRules()
    {
        var set = new PermissionRuleSet([PermissionRule.ForExact("Read", """{"file_path":"a.txt"}""")]);

        set.IsAlwaysAllowed("Read", """{"file_path":"a.txt"}""").Should().BeTrue();
        set.IsAlwaysAllowed("Read", """{"file_path":"b.txt"}""").Should().BeFalse();
    }
}

using Cockpit.Core.Plugins;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>Normalizing a plugin id into a filesystem-safe folder slug (#14), with empty as the "fall back to an installation id" signal.</summary>
public class PluginFolderNameTests
{
    [Theory]
    [InlineData("github-issues", "github-issues")]
    [InlineData("GitHub Issues", "github-issues")]
    [InlineData("  Weird__Name!! ", "weird-name")]
    [InlineData("a.b.c", "a-b-c")]
    [InlineData("UPPER", "upper")]
    public void Normalize_ProducesLowercaseSlug(string input, string expected)
    {
        Assert.Equal(expected, PluginFolderName.Normalize(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    [InlineData("---")]
    public void Normalize_NothingUsable_ReturnsEmpty(string input)
    {
        Assert.Empty(PluginFolderName.Normalize(input));
    }
}

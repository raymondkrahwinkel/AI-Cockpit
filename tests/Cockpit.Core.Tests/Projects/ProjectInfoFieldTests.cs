using FluentAssertions;
using Cockpit.Core.Projects;

namespace Cockpit.Core.Tests.Projects;

/// <summary>One row of a project's extra information: when it counts as empty, when its value is a link, and what tidying it does.</summary>
public class ProjectInfoFieldTests
{
    [Fact]
    public void IsBlank_OnlyWhenBothHalvesAreEmpty()
    {
        new ProjectInfoField("  ", "\t").IsBlank.Should().BeTrue("an untouched row the editor added carries nothing");
        new ProjectInfoField("Repository", "").IsBlank.Should().BeFalse();
        new ProjectInfoField("", "https://example.com").IsBlank
            .Should().BeFalse("a pasted link with no label yet is still information");
        new ProjectInfoField("   ", "https://example.com").IsBlank
            .Should().BeFalse("a label of nothing but spaces is the same as no label — the value still counts");
    }

    [Fact]
    public void Normalized_TwoRowsWithTheSameLabel_AreBothKept()
    {
        // Deliberately not a dictionary: two contacts are two rows, and rejecting the second would be the model
        // telling the operator their own labels are wrong.
        var settings = ProjectSettings.Empty.WithProject(Project.Create("Cockpit") with
        {
            AdditionalInfo =
            [
                new ProjectInfoField("Contact", "Acme BV service desk"),
                new ProjectInfoField("Contact", "Acme BV account manager"),
            ],
        });

        settings.Normalized().Projects.Should().ContainSingle()
            .Which.AdditionalInfo.Select(field => field.Value).Should().Equal("Acme BV service desk", "Acme BV account manager");
    }

    [Theory]
    [InlineData("https://github.com/example/repo")]
    [InlineData("http://example.test")]
    public void IsWebLink_TrueForHttpAndHttps(string value) =>
        new ProjectInfoField("Repository", value).IsWebLink.Should().BeTrue();

    [Theory]
    [InlineData("github.com/example/repo")]
    [InlineData("file:///etc/passwd")]
    [InlineData("mailto:someone@example.test")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    [InlineData("Ask the service desk, they sign off on it")]
    public void IsWebLink_FalseForAnythingElse(string value) =>
        new ProjectInfoField("Note", value).IsWebLink
            .Should().BeFalse("only http(s) is ever handed to the shell, so only http(s) may look followable");

    [Fact]
    public void Tidied_TrimsTheLabelAndFoldsThePastedValueOntoOneLine()
    {
        var tidied = new ProjectInfoField("  Contact  ", "Acme BV\r\n  service desk\n\n").Tidied();

        tidied.Label.Should().Be("Contact");
        tidied.Value.Should().Be("Acme BV service desk");
    }

    [Theory]
    [InlineData(0x0A)]
    [InlineData(0x0B)]
    [InlineData(0x0C)]
    [InlineData(0x0D)]
    [InlineData(0x85)]
    [InlineData(0x2028)]
    [InlineData(0x2029)]
    public void Tidied_FoldsEveryHardBreakAPasteCanBring(int codePoint)
    {
        // Every one of these is a mandatory line break to Avalonia's text layout, and a wrapping text block over a
        // value that still contains one never finishes measuring — it allocates until the process is killed. Missing a
        // single character from this set leaves that crash reachable, so each one gets its own case.
        var pasted = "Acme BV" + (char)codePoint + "Amsterdam";

        new ProjectInfoField("Customer", pasted).Tidied().Value.Should().Be("Acme BV Amsterdam");
    }

    [Fact]
    public void Tidied_StripsTheInvisibleMarksThatMakeAValueReadAsSomethingElse()
    {
        // A row's value is both what the link says and where it goes. A right-to-left override renders the text
        // reversed while the click still follows the real address — the display and the target would disagree.
        var deceptive = "https://example.test/" + (char)0x202E + "gnp.evil" + (char)0x200B;

        var tidied = new ProjectInfoField("Repository" + (char)0x200E, deceptive).Tidied();

        tidied.Value.Should().Be("https://example.test/gnp.evil");
        tidied.Label.Should().Be("Repository");
    }

    [Fact]
    public void Tidied_LeavesAnOrdinaryRowAsItIs()
    {
        var field = new ProjectInfoField("Repository", "https://github.com/example/repo");

        field.Tidied().Should().Be(field);
    }
}

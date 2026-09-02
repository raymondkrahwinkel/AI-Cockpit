using Cockpit.Core.Projects;

namespace Cockpit.Core.Tests.Projects;

/// <summary>One row of a project's extra information: when it counts as empty, when its value is a link, and what tidying it does.</summary>
public class ProjectInfoFieldTests
{
    [Fact]
    public void ASecretRow_NeverReachesASessionEvenWhenSharingIsTicked()
    {
        // The two flags are answered together on the model rather than left to each surface: a token in a system prompt
        // is the thing this exists to prevent, and one caller forgetting to check both would undo it.
        var secret = new ProjectInfoField("Deploy token", "s3cr3t")
        {
            IsSecret = true,
            IsSharedWithSessions = true,
        };

        Assert.False(secret.ReachesSessions, "a credential is never told to a session");
        Assert.True(
            new ProjectInfoField("Repository", "https://example.test") { IsSharedWithSessions = true }.ReachesSessions,
            "an ordinary shared row still is");
    }

    [Fact]
    public void ASecretRow_IsNeverDrawnAsAFollowableLink()
    {
        // A secret that happens to parse as a URL would otherwise get a link carrying the value in its tooltip, and a
        // click would put it in the browser's history.
        Assert.False(new ProjectInfoField("Webhook", "https://hooks.example.test/T0K3N") { IsSecret = true }.IsWebLink);
    }

    [Fact]
    public void ASecretRow_IsNotShownAsPlainText()
    {
        Assert.False(new ProjectInfoField("Deploy token", "s3cr3t") { IsSecret = true }.ShowsPlainValue);
        Assert.True(new ProjectInfoField("Customer", "Acme BV").ShowsPlainValue);
        Assert.False(
            new ProjectInfoField("Repository", "https://example.test").ShowsPlainValue,
            "a web address is drawn as a link instead");
    }

    [Fact]
    public void IsBlank_OnlyWhenBothHalvesAreEmpty()
    {
        Assert.True(new ProjectInfoField("  ", "\t").IsBlank, "an untouched row the editor added carries nothing");
        Assert.False(new ProjectInfoField("Repository", "").IsBlank);
        Assert.False(
            new ProjectInfoField("", "https://example.com").IsBlank,
            "a pasted link with no label yet is still information");
        Assert.False(
            new ProjectInfoField("   ", "https://example.com").IsBlank,
            "a label of nothing but spaces is the same as no label — the value still counts");
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

        var project = Assert.Single(settings.Normalized().Projects);
        Assert.Equal(
            new[] { "Acme BV service desk", "Acme BV account manager" },
            project.AdditionalInfo.Select(field => field.Value));
    }

    [Theory]
    [InlineData("https://github.com/example/repo")]
    [InlineData("http://example.test")]
    public void IsWebLink_TrueForHttpAndHttps(string value) =>
        Assert.True(new ProjectInfoField("Repository", value).IsWebLink);

    [Theory]
    [InlineData("github.com/example/repo")]
    [InlineData("file:///etc/passwd")]
    [InlineData("mailto:someone@example.test")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    [InlineData("Ask the service desk, they sign off on it")]
    public void IsWebLink_FalseForAnythingElse(string value) =>
        Assert.False(
            new ProjectInfoField("Note", value).IsWebLink,
            "only http(s) is ever handed to the shell, so only http(s) may look followable");

    [Fact]
    public void Tidied_TrimsTheLabelAndFoldsThePastedValueOntoOneLine()
    {
        var tidied = new ProjectInfoField("  Contact  ", "Acme BV\r\n  service desk\n\n").Tidied();

        Assert.Equal("Contact", tidied.Label);
        Assert.Equal("Acme BV service desk", tidied.Value);
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

        Assert.Equal("Acme BV Amsterdam", new ProjectInfoField("Customer", pasted).Tidied().Value);
    }

    [Fact]
    public void Tidied_StripsTheInvisibleMarksThatMakeAValueReadAsSomethingElse()
    {
        // A row's value is both what the link says and where it goes. A right-to-left override renders the text
        // reversed while the click still follows the real address — the display and the target would disagree.
        var deceptive = "https://example.test/" + (char)0x202E + "gnp.evil" + (char)0x200B;

        var tidied = new ProjectInfoField("Repository" + (char)0x200E, deceptive).Tidied();

        Assert.Equal("https://example.test/gnp.evil", tidied.Value);
        Assert.Equal("Repository", tidied.Label);
    }

    [Fact]
    public void Tidied_KeepsWhetherTheRowIsSharedWithSessions()
    {
        // Normalized() tidies on every load and every save, so a positional `new(Label, Value)` in here — which carries
        // only those two — would have quietly unticked every row the operator shared.
        var shared = new ProjectInfoField("  Repository ", " https://github.com/example/repo ")
        {
            IsSharedWithSessions = true,
        };

        Assert.True(shared.Tidied().IsSharedWithSessions, "tidying a row must not change what it is for");
    }

}

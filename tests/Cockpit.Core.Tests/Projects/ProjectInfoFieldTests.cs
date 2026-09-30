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

}

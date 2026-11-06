using Cockpit.Core.Mcp;

namespace Cockpit.Core.Tests.Mcp;

// Pins criterion 5 of AC-794: what a scoped controller is told about a profile is a deliberate allow-list, not
// "whatever SessionProfile happens to carry minus what looks secret-shaped". Same idiom as
// SharedProjectPublishDefinitionSecrecyTests — these tests make the rule impossible to break by accident, not
// implement it.
public class NodeScopedProfileSummarySecrecyTests
{
    [Fact]
    public void Summary_CarriesNoProviderConfigOrEnvironmentOrWorkingDirectoryOrSystemPrompt()
    {
        var names = typeof(NodeScopedProfileSummary).GetProperties().Select(property => property.Name).ToArray();

        // Named explicitly rather than pattern-matched: these are the SessionProfile fields that are not
        // secret-shaped by SecretFields' naming rule and would therefore slip through an "everything but secrets"
        // filter — the exact gap criterion 5 exists to close.
        Assert.DoesNotContain(nameof(Cockpit.Core.Profiles.SessionProfile.ProviderConfig), names);
        Assert.DoesNotContain(nameof(Cockpit.Core.Profiles.SessionProfile.EnvironmentVariables), names);
        Assert.DoesNotContain(nameof(Cockpit.Core.Profiles.SessionProfile.DefaultWorkingDirectory), names);
        Assert.DoesNotContain(nameof(Cockpit.Core.Profiles.SessionProfile.SystemPrompt), names);
        Assert.DoesNotContain(nameof(Cockpit.Core.Profiles.SessionProfile.Claude), names);
    }
}

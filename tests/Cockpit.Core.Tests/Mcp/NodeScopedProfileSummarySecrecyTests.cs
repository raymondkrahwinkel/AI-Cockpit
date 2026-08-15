using Cockpit.Core.Mcp;

namespace Cockpit.Core.Tests.Mcp;

// Pins criterion 5 of AC-794: what a scoped controller is told about a profile is a deliberate allow-list, not
// "whatever SessionProfile happens to carry minus what looks secret-shaped". Same idiom as
// SharedProjectPublishDefinitionSecrecyTests — these tests make the rule impossible to break by accident, not
// implement it.
public class NodeScopedProfileSummarySecrecyTests
{
    private static readonly string[] _FieldsClearedForScoping =
    [
        nameof(NodeScopedProfileSummary.Label),
        nameof(NodeScopedProfileSummary.Provider),
        nameof(NodeScopedProfileSummary.Purpose),
    ];

    [Fact]
    public void Summary_CarriesOnlyFieldsClearedForScoping_SoAddingOneIsADeliberateAct()
    {
        var actual = typeof(NodeScopedProfileSummary)
            .GetProperties()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal);

        Assert.Equal(_FieldsClearedForScoping.OrderBy(name => name, StringComparer.Ordinal), actual);
    }

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

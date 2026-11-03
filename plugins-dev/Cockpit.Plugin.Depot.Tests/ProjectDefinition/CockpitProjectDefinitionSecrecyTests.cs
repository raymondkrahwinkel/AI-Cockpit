using Cockpit.Plugin.Depot.ProjectDefinition;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Plugin.Depot.Tests.ProjectDefinition;

// A sensitive value never reaches Depot in the clear: every type crossing to Depot carries exactly the fields listed
// here, so adding one turns this red and forces the question whether it can carry a secret (AC-618, AC-1071, AC-246).
// AC-607's SensitiveFields/PasswordEnvelope travel as ciphertext only (CockpitProjectSensitiveFieldFilter.Apply).
public class CockpitProjectDefinitionSecrecyTests
{
    [Theory]
    [InlineData(typeof(CockpitProjectDefinition),
        nameof(CockpitProjectDefinition.SchemaVersion), nameof(CockpitProjectDefinition.Name),
        nameof(CockpitProjectDefinition.Description), nameof(CockpitProjectDefinition.GitUrl),
        nameof(CockpitProjectDefinition.BehaviorPrompt), nameof(CockpitProjectDefinition.IsolateInWorktreeByDefault),
        nameof(CockpitProjectDefinition.McpOverlay), nameof(CockpitProjectDefinition.Resources),
        nameof(CockpitProjectDefinition.Logo), nameof(CockpitProjectDefinition.ExtensionData),
        nameof(CockpitProjectDefinition.SensitiveFields), nameof(CockpitProjectDefinition.PasswordEnvelope))]
    [InlineData(typeof(CockpitProjectResourceEntry),
        nameof(CockpitProjectResourceEntry.Role), nameof(CockpitProjectResourceEntry.Reference),
        nameof(CockpitProjectResourceEntry.Label), nameof(CockpitProjectResourceEntry.Portability),
        nameof(CockpitProjectResourceEntry.ExtensionData), nameof(CockpitProjectResourceEntry.Placeholder))]
    [InlineData(typeof(CockpitProjectSensitiveFieldEntry),
        nameof(CockpitProjectSensitiveFieldEntry.Label), nameof(CockpitProjectSensitiveFieldEntry.Value),
        nameof(CockpitProjectSensitiveFieldEntry.ExtensionData))]
    [InlineData(typeof(CockpitProjectPasswordEnvelope),
        nameof(CockpitProjectPasswordEnvelope.Kdf), nameof(CockpitProjectPasswordEnvelope.Iterations),
        nameof(CockpitProjectPasswordEnvelope.Password), nameof(CockpitProjectPasswordEnvelope.Recovery))]
    [InlineData(typeof(CockpitProjectKeyWrapper),
        nameof(CockpitProjectKeyWrapper.Salt), nameof(CockpitProjectKeyWrapper.WrappedDataKey))]
    [InlineData(typeof(SharedProjectPublishDefinition),
        nameof(SharedProjectPublishDefinition.Name), nameof(SharedProjectPublishDefinition.Description),
        nameof(SharedProjectPublishDefinition.GitUrl), nameof(SharedProjectPublishDefinition.BehaviorPrompt),
        nameof(SharedProjectPublishDefinition.IsolateInWorktreeByDefault), nameof(SharedProjectPublishDefinition.EnabledMcpServerNames),
        nameof(SharedProjectPublishDefinition.Resources), nameof(SharedProjectPublishDefinition.LogoBytes))]
    [InlineData(typeof(SharedProjectPublishResource),
        nameof(SharedProjectPublishResource.Role), nameof(SharedProjectPublishResource.Reference),
        nameof(SharedProjectPublishResource.Label))]
    public void SharedType_CarriesOnlyFieldsClearedForSharing_SoAddingOneIsADeliberateAct(Type sharedType, params string[] cleared)
    {
        var actual = sharedType
            .GetProperties()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal);

        Assert.Equal(cleared.OrderBy(name => name, StringComparer.Ordinal), actual);
    }
}

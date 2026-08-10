using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Plugin.Depot.Tests.ProjectDefinition;

// Pins the same rule CockpitProjectDefinitionSecrecyTests pins for the write side, one boundary earlier: a secret
// AdditionalInfo row must never reach the type ISharedProjectSource.PublishAsync (AC-620) is handed. These tests
// make that impossible to break by accident, not implement the rule itself.
public class SharedProjectPublishDefinitionSecrecyTests
{
    private static readonly string[] _FieldsClearedForSharing =
    [
        nameof(SharedProjectPublishDefinition.Name),
        nameof(SharedProjectPublishDefinition.Description),
        nameof(SharedProjectPublishDefinition.GitUrl),
        nameof(SharedProjectPublishDefinition.BehaviorPrompt),
        nameof(SharedProjectPublishDefinition.IsolateInWorktreeByDefault),
        nameof(SharedProjectPublishDefinition.EnabledMcpServerNames),
        nameof(SharedProjectPublishDefinition.Resources),
    ];

    [Fact]
    public void Definition_CarriesOnlyFieldsClearedForSharing_SoAddingOneIsADeliberateAct()
    {
        var actual = typeof(SharedProjectPublishDefinition)
            .GetProperties()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal);

        Assert.Equal(_FieldsClearedForSharing.OrderBy(name => name, StringComparer.Ordinal), actual);
    }

    [Fact]
    public void Definition_CarriesNoAdditionalInfo_SinceThoseRowsAreWhereSecretsLive()
    {
        var names = typeof(SharedProjectPublishDefinition).GetProperties().Select(property => property.Name).ToArray();

        Assert.DoesNotContain(names, name => name.Contains("Secret", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("AdditionalInfo", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PublishResource_CarriesOnlyFieldsClearedForSharing_SoARowCannotGrowASecretUnnoticed()
    {
        string[] cleared =
        [
            nameof(SharedProjectPublishResource.Role),
            nameof(SharedProjectPublishResource.Reference),
            nameof(SharedProjectPublishResource.Label),
        ];

        var actual = typeof(SharedProjectPublishResource)
            .GetProperties()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal);

        Assert.Equal(cleared.OrderBy(name => name, StringComparer.Ordinal), actual);
    }
}

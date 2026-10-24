using Cockpit.Plugin.Depot.ProjectDefinition;

namespace Cockpit.Plugin.Depot.Tests.ProjectDefinition;

// AC-246: covers an older build reading a definition a newer Cockpit wrote with a placeholder row it doesn't
// know. Since this build cannot literally run an older assembly, an unrecognized field stands in via
// `ExtensionData`; these tests assert no exception, no lost `Reference`, unknown field forwarded on re-write.
public class CockpitProjectDefinitionForwardCompatTests
{
    [Fact]
    public void TryDeserialize_ARowCarryingAFieldThisBuildDoesNotRecognise_ParsesCleanlyAndForwardsIt()
    {
        // Simulates what an actually-older build (one predating AC-246's own Placeholder property) would face
        // reading a row a newer one wrote: an unrecognised key, no crash, forwarded rather than lost.
        const string json = """{"schemaVersion":1,"name":"probe","resources":[{"role":"Reference","label":"Productie-DB","someFutureResourceField":"forwarded as-is"}]}""";

        var succeeded = CockpitProjectDefinitionJson.TryDeserialize(json, out var definition, out var error);

        Assert.True(succeeded, error);
        Assert.NotNull(definition);
        Assert.NotNull(definition.Resources);
        var resource = Assert.Single(definition.Resources);
        Assert.Equal(string.Empty, resource.Reference);
        Assert.False(resource.Placeholder); // unrecognised to THIS build's own "placeholder" key — false is correct, not a guess
        Assert.Contains("someFutureResourceField", CockpitProjectDefinitionJson.Serialize(definition));
    }

    [Fact]
    public void Serialize_AnOrdinaryNonPlaceholderRow_OmitsThePlaceholderKeyEntirely()
    {
        // JsonIgnore(WhenWritingDefault): an older reader must see nothing different about the overwhelmingly
        // common row at all — only the new machine-scope case grows a key it has never seen.
        var written = CockpitProjectDefinitionJson.Serialize(new CockpitProjectDefinition
        {
            Name = "probe",
            Resources = [CockpitProjectResourceEntry.Create("Instructions", "docs/CONVENTIONS.md") ?? throw new InvalidOperationException("The resource row was refused.")],
        });

        Assert.DoesNotContain("placeholder", written, StringComparison.OrdinalIgnoreCase);
    }
}

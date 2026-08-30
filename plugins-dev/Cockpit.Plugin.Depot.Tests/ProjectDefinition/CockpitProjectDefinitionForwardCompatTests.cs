using Cockpit.Plugin.Depot.ProjectDefinition;

namespace Cockpit.Plugin.Depot.Tests.ProjectDefinition;

// AC-246: covers an older build reading a definition a newer Cockpit wrote with a placeholder row it doesn't
// know. Since this build cannot literally run an older assembly, an unrecognized field stands in via
// `ExtensionData`; these tests assert no exception, no lost `Reference`, unknown field forwarded on re-write.
public class CockpitProjectDefinitionForwardCompatTests
{
    [Fact]
    public void TryDeserialize_APlaceholderRowWithNoReferenceKeyAtAll_ParsesWithoutThrowing()
    {
        const string json = """{"schemaVersion":1,"name":"probe","resources":[{"role":"Reference","label":"Productie-DB","placeholder":true}]}""";

        var succeeded = CockpitProjectDefinitionJson.TryDeserialize(json, out var definition, out var error);

        Assert.True(succeeded, error);
        var resource = Assert.Single(definition!.Resources!);
        Assert.Equal(string.Empty, resource.Reference); // the C# default, not null and not a missing-property failure
        Assert.True(resource.Placeholder);
        Assert.Equal("Productie-DB", resource.Label);
    }

    [Fact]
    public void TryDeserialize_ARowCarryingAFieldThisBuildDoesNotRecognise_ParsesCleanlyAndForwardsIt()
    {
        // Simulates what an actually-older build (one predating AC-246's own Placeholder property) would face
        // reading a row a newer one wrote: an unrecognised key, no crash, forwarded rather than lost.
        const string json = """{"schemaVersion":1,"name":"probe","resources":[{"role":"Reference","label":"Productie-DB","someFutureResourceField":"forwarded as-is"}]}""";

        var succeeded = CockpitProjectDefinitionJson.TryDeserialize(json, out var definition, out var error);

        Assert.True(succeeded, error);
        var resource = Assert.Single(definition!.Resources!);
        Assert.Equal(string.Empty, resource.Reference);
        Assert.False(resource.Placeholder); // unrecognised to THIS build's own "placeholder" key — false is correct, not a guess
        Assert.Contains("someFutureResourceField", CockpitProjectDefinitionJson.Serialize(definition));
    }

    [Fact]
    public void TryDeserialize_APlaceholderRow_NeverProducesABlankRowWithNoRoleOrLabelEither()
    {
        // The failure mode this whole design exists to avoid: a row that carries no reference AND says nothing
        // else about itself either — that would be indistinguishable from data loss. Role and Label are what a
        // placeholder still has to say.
        const string json = """{"schemaVersion":1,"name":"probe","resources":[{"role":"Instructions","label":"Runbook","placeholder":true}]}""";

        Assert.True(CockpitProjectDefinitionJson.TryDeserialize(json, out var definition, out _));
        var resource = Assert.Single(definition!.Resources!);
        Assert.False(string.IsNullOrEmpty(resource.Role));
        Assert.False(string.IsNullOrEmpty(resource.Label));
    }

    [Fact]
    public void SerializeThenDeserialize_APlaceholderRow_RoundTripsWithoutLoss()
    {
        var reference = OperatingSystem.IsWindows() ? @"C:\Users\erik\notes.md" : "/home/erik/notes.md";
        var written = CockpitProjectDefinitionJson.Serialize(new CockpitProjectDefinition
        {
            Name = "probe",
            Resources = [CockpitProjectResourceEntry.Create("Reference", reference, "Erik's notes")!],
        });

        Assert.True(CockpitProjectDefinitionJson.TryDeserialize(written, out var readBack, out var error));
        var resource = Assert.Single(readBack!.Resources!);
        Assert.True(resource.Placeholder);
        Assert.Equal("Erik's notes", resource.Label);
        Assert.Equal(string.Empty, resource.Reference);
    }

    [Fact]
    public void Serialize_AnOrdinaryNonPlaceholderRow_OmitsThePlaceholderKeyEntirely()
    {
        // JsonIgnore(WhenWritingDefault): an older reader must see nothing different about the overwhelmingly
        // common row at all — only the new machine-scope case grows a key it has never seen.
        var written = CockpitProjectDefinitionJson.Serialize(new CockpitProjectDefinition
        {
            Name = "probe",
            Resources = [CockpitProjectResourceEntry.Create("Instructions", "docs/CONVENTIONS.md")!],
        });

        Assert.DoesNotContain("placeholder", written, StringComparison.OrdinalIgnoreCase);
    }
}

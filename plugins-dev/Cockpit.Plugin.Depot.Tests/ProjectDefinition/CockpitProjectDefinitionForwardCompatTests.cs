using Cockpit.Plugin.Depot.ProjectDefinition;

namespace Cockpit.Plugin.Depot.Tests.ProjectDefinition;

// AC-246 (Raymond, 2026-08-02): the one place a placeholder row can do damage — an older build that has never
// heard of `CockpitProjectResourceEntry.Placeholder`, reading a definition a newer Cockpit wrote.
// Measured through the real deserializer (`CockpitProjectDefinitionJson.TryDeserialize`), the same
// technique `CockpitProjectDefinitionSecrecyTests.ExtensionData_ForwardsUnknownFieldsUnread_WhichIsTheGapAnEncryptionDesignMustClose`
// already uses for the top-level case: this build cannot literally run an older assembly, so a field this build
// itself does not recognise stands in for one an older build would not recognise either — the same
// `CockpitProjectResourceEntry.ExtensionData` mechanism catches both.
//
// What has to hold, and what these tests measure rather than assume: no exception, no lost `Reference`
// property (it is a required-shaped C# string with a default, never a nullable the deserializer could refuse to
// populate), and the unknown field forwarded rather than silently dropped on a later re-write. None of this
// reaches `cockpit.json` (the *local* on-disk file, a completely different type in
// `Cockpit.Infrastructure.Configuration`) at all — `CockpitProjectResourceEntry` is
// `.cockpit/project.json`'s own wire shape, read from Depot, so the "unknown enum value costs the whole
// local config a `.damaged-` fallback" risk this repo's own build traps warn about for local entries simply
// does not apply here: nothing here is an enum, and nothing here is local.
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
        var written = CockpitProjectDefinitionJson.Serialize(new CockpitProjectDefinition
        {
            Name = "probe",
            Resources = [CockpitProjectResourceEntry.Create("Reference", "/home/erik/notes.md", "Erik's notes")!],
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

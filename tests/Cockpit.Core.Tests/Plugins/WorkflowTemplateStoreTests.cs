using Cockpit.Core.Plugins;
using FluentAssertions;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// The workflow templates a store offers (#69). They ride in the same <c>index.json</c> as the plugins: a store that
/// publishes both is one store, and asking the operator to visit two places to see what it has would be plumbing
/// leaking into the app.
/// <para>
/// A template is not code — text, not an assembly, nothing loaded and nothing run — so it needs no consent prompt and
/// no hash pinned against a running process. It still needs a checksum: what arrives has to be what was published.
/// </para>
/// </summary>
public class WorkflowTemplateStoreTests
{
    [Fact]
    public void AnIndex_CarriesTheTemplatesAStoreOffers()
    {
        const string json = """
        {
          "name": "Cockpit plugins",
          "plugins": [],
          "templates": [
            {
              "id": "raymond.ticket-to-agent",
              "name": "Ticket → branch → agent",
              "description": "Pick a ticket, cut the branch, put an agent on it.",
              "author": "raymond",
              "version": "1.1",
              "path": "templates/ticket-to-agent.json",
              "sha256": "abc123",
              "requires": ["youtrack"]
            }
          ]
        }
        """;

        PluginStoreIndex.TryParse(json, out var index, out var error).Should().BeTrue(error);

        var template = index!.Templates!.Single();
        template.Id.Should().Be("raymond.ticket-to-agent");
        template.Path.Should().Be("templates/ticket-to-agent.json");
        template.Sha256.Should().Be("abc123");
        template.Requires.Should().Equal("youtrack");
    }

    // Every store published before templates existed has no "templates" key. It must still parse — the plugins it
    // offers are the whole point of it.
    [Fact]
    public void AnIndexWithoutTemplates_StillParses_AndOffersNone()
    {
        const string json = """
        {
          "name": "Cockpit plugins",
          "plugins": [
            { "id": "youtrack", "name": "YouTrack", "latestVersion": "1.12.0", "versions": [] }
          ]
        }
        """;

        PluginStoreIndex.TryParse(json, out var index, out var error).Should().BeTrue(error);

        index!.Plugins.Should().ContainSingle();
        index.Templates.Should().BeEmpty();
    }

    [Fact]
    public void AnIndexWithNeitherPluginsNorTemplates_ParsesToEmptyLists_RatherThanNulls()
    {
        PluginStoreIndex.TryParse("""{ "name": "Empty" }""", out var index, out _).Should().BeTrue();

        index!.Plugins.Should().BeEmpty();
        index.Templates.Should().BeEmpty();
    }
}

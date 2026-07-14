using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Infrastructure.Plugins;
using FluentAssertions;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// The templates installed from a store, on disk (#69). Installing one writes a file — there is no assembly to load
/// and no code to consent to — and the cockpit reads them at startup into the same picker the plugins' own templates
/// go into, because to the operator they are one kind of thing: a flow somebody already drew.
/// </summary>
public class WorkflowTemplateLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void AnInstalledTemplate_IsThereTheNextTimeTheCockpitLooks()
    {
        var library = new WorkflowTemplateLibrary(_root);

        library.Install(_Template("raymond.ticket-to-agent"));

        // A fresh library over the same directory is what the next launch sees.
        var loaded = new WorkflowTemplateLibrary(_root).Load().Single();
        loaded.Id.Should().Be("raymond.ticket-to-agent");
        loaded.Name.Should().Be("Ticket → branch → agent");
        loaded.Json.Should().Contain("cockpit.command");
        loaded.Requires.Should().Equal("youtrack");
    }

    [Fact]
    public void InstallingTheSameTemplateAgain_ReplacesIt_RatherThanKeepingTwo()
    {
        var library = new WorkflowTemplateLibrary(_root);
        library.Install(_Template("raymond.ticket-to-agent") with { Version = "1.0" });

        library.Install(_Template("raymond.ticket-to-agent") with { Version = "1.1" });

        library.Load().Single().Version.Should().Be("1.1");
    }

    [Fact]
    public void ARemovedTemplate_IsGone()
    {
        var library = new WorkflowTemplateLibrary(_root);
        library.Install(_Template("raymond.ticket-to-agent"));

        library.Remove("raymond.ticket-to-agent");

        library.IsInstalled("raymond.ticket-to-agent").Should().BeFalse();
        library.Load().Should().BeEmpty();
    }

    // A store's id is a string the cockpit did not write, and it is used as a file name. One that tries to climb out of
    // the template directory writes inside it anyway.
    [Fact]
    public void AnIdThatTriesToEscapeTheDirectory_WritesInsideItAnyway()
    {
        var library = new WorkflowTemplateLibrary(_root);

        library.Install(_Template("../../evil"));

        Directory.GetFiles(_root).Should().ContainSingle()
            .Which.Should().StartWith(_root, "a template is written where templates live, wherever its id points");
    }

    // A hand-edited or half-written file costs the operator that template, not the library.
    [Fact]
    public void AFileThatIsNotATemplate_IsSkipped_AndTheRestStillLoad()
    {
        var library = new WorkflowTemplateLibrary(_root);
        library.Install(_Template("raymond.ticket-to-agent"));
        File.WriteAllText(Path.Combine(_root, "broken.json"), "{ this is not json");

        library.Load().Should().ContainSingle().Which.Id.Should().Be("raymond.ticket-to-agent");
    }

    private static InstalledWorkflowTemplate _Template(string id) => new(
        id,
        "Ticket → branch → agent",
        "Pick a ticket, cut the branch, put an agent on it.",
        """{ "Id": "t", "Name": "Ticket → branch → agent", "Nodes": [ { "Id": "a", "TypeId": "cockpit.command", "Name": "Cut the branch" } ], "Connections": [] }""",
        Author: "raymond",
        Version: "1.0",
        Category: "Cockpit plugins",
        Requires: ["youtrack"]);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}

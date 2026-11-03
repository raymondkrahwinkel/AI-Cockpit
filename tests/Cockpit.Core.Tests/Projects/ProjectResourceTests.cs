using System.Text.Json.Nodes;
using Cockpit.Core.Projects;
using Cockpit.Infrastructure.Projects;

namespace Cockpit.Core.Tests.Projects;

/// <summary>
/// A project's resources (AC-483): the list that replaces the single <see cref="Project.MemoryRef"/> it used to
/// carry, and the migration that turns an old <c>cockpit.json</c> with just that field into one row of it. Covers
/// the six acceptance criteria; the persistence tests round-trip through a real temporary config file the way
/// <c>ProjectStoreTests</c> and <c>ProjectPluginLinkTests</c> already do.
/// </summary>
public class ProjectResourceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configFilePath;

    public ProjectResourceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configFilePath = Path.Combine(_tempDir, "cockpit.json");
    }

    // --- AC1: an old cockpit.json with only the flat memoryRef migrates to one Memory resource -------------------

    [Fact]
    public async Task SaveAsync_ALegacyMemoryRefLoadedAndSavedUnchanged_PreservesItsMeaning()
    {
        await File.WriteAllTextAsync(
            _configFilePath,
            """{"Projects":[{"Id":"legacy","Name":"Cockpit","MemoryRef":"depot:ai-cockpit"}]}""");

        var store = new ProjectStore(_configFilePath);
        var loaded = await store.LoadAsync();
        await store.SaveAsync(loaded);
        var reloaded = await store.LoadAsync();

        var project = Assert.Single(reloaded.Projects);
        Assert.Equal("depot:ai-cockpit", project.MemoryRef);
        Assert.Equal(new ProjectResource("depot:ai-cockpit", ProjectResourceRole.Memory), Assert.Single(project.Resources));

        // The real contract of this save, pinned directly rather than by a "does the raw text contain a substring"
        // proxy: Resources is what a project's memory now lives under, and the legacy MemoryRef field is written
        // alongside it — deliberately, and only until AC-485 — mirroring the exact same value rather than either
        // being dropped or disagreeing with the other. Flipping ProjectEntry.FromDomain back to `MemoryRef = null`
        // (the shape this test used to accept) must fail this, which "written.Should().Contain(\"Resources\")"
        // alone could not: that string appears whether or not MemoryRef is written beside it.
        var writtenEntry = JsonNode.Parse(await File.ReadAllTextAsync(_configFilePath))!["Projects"]![0]!;
        Assert.NotNull(writtenEntry["Resources"]);
        Assert.Equal("depot:ai-cockpit", writtenEntry["MemoryRef"]!.GetValue<string>());
    }

    // --- MUST-FIX 2: a missing or unrecognised Role never resolves to Memory, and never costs the whole file --------

    /// <summary>
    /// An unrecognised <c>role</c> — a typo, or a role only a newer build knows — must not throw for the whole
    /// document: before this fix, the shared <see cref="System.Text.Json.Serialization.JsonStringEnumConverter"/>
    /// failing on this one value made <c>TryReadAsync</c> return null for the entire file, which
    /// <c>CockpitConfigFileAccess</c> then treats as "the live file is damaged" — moving it aside and restoring the
    /// <c>.bak</c>, losing every other section (profiles, MCP servers, plugin storage) written since. This is the
    /// important half of the fix: proving the bad row costs only itself, not the rest of the config.
    /// </summary>
    [Fact]
    public async Task LoadAsync_OlderOrForeignShapes_LoadWithoutCostingTheRestOfTheFile()
    {
        await File.WriteAllTextAsync(
            _configFilePath,
            """
            {
              "Projects":[
                {"Id":"p","Name":"Cockpit","Resources":[{"Reference":"D:\\handbook","Role":"Instruction"}]},
                {"Id":"q","Name":"Second project"},
                {"Id":"r","Name":"Both","MemoryRef":"depot:stale","Resources":[{"Reference":"depot:current","Role":"Memory"}]},
                {"Id":"s","Name":"Legacy","SourceDirectory":"/home/raymond/legacy"}
              ]
            }
            """);

        var loaded = await new ProjectStore(_configFilePath).LoadAsync();

        Assert.Equal(4, System.Linq.Enumerable.Count(loaded.Projects));
        Assert.Equal(
            ProjectResourceRole.Reference,
            Assert.Single(loaded.Projects.Single(project => project.Id == "p").Resources).Role);
        Assert.Equal("Second project", loaded.Projects.Single(project => project.Id == "q").Name);

        // FIX 7: a file carrying both the legacy MemoryRef and the list trusts Resources, the fuller answer.
        var both = loaded.Projects.Single(project => project.Id == "r");
        Assert.Equal(new ProjectResource("depot:current", ProjectResourceRole.Memory), Assert.Single(both.Resources));
        Assert.Equal("depot:current", both.MemoryRef);

        // AC-938: a project from before multi-repository only wrote the singular SourceDirectory — it loads as
        // exactly the one repository it always described.
        var legacy = loaded.Projects.Single(project => project.Id == "s");
        Assert.Equal("/home/raymond/legacy", legacy.SourceDirectory);
        var repository = Assert.Single(legacy.SourceDirectories);
        Assert.Equal("/home/raymond/legacy", repository.Path);
        Assert.Null(repository.Label);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}

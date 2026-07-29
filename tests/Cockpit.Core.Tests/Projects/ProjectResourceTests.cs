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
    public async Task LoadAsync_ALegacyMemoryRef_LoadsAsOneMemoryResource()
    {
        await File.WriteAllTextAsync(
            _configFilePath,
            """{"Projects":[{"Id":"legacy","Name":"Cockpit","MemoryRef":"depot:ai-cockpit"}]}""");

        var loaded = await new ProjectStore(_configFilePath).LoadAsync();

        var project = Assert.Single(loaded.Projects);
        Assert.Equal(new ProjectResource("depot:ai-cockpit", ProjectResourceRole.Memory), Assert.Single(project.Resources));
        Assert.Equal("depot:ai-cockpit", project.MemoryRef);
    }

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

    // --- AC2: two rows with the same role round-trip in order ------------------------------------------------------

    [Fact]
    public async Task SaveAsync_TwoMemoryRows_RoundTripsBothInOrder()
    {
        var project = Project.Create("Cockpit") with
        {
            Resources =
            [
                new ProjectResource("depot:ai-cockpit", ProjectResourceRole.Memory),
                new ProjectResource("/home/raymond/Notes/Cockpit", ProjectResourceRole.Memory),
            ],
        };

        var store = new ProjectStore(_configFilePath);
        await store.SaveAsync(ProjectSettings.Empty.WithProject(project));
        var loaded = await store.LoadAsync();

        Assert.Equal(project.Resources, Assert.Single(loaded.Projects).Resources);
    }

    // --- AC3: a resource whose plugin is not installed is written through untouched --------------------------------

    /// <summary>
    /// Named for what it actually exercises — <c>ProjectStore</c>'s load/save round trip — rather than the broader
    /// "…IsWrittenThroughUntouched" this used to claim. It cites the same rule <c>PluginFields</c> follows
    /// (Project.cs, LinkedAs/PluginFields doc comment), but only through this one store; the path that actually
    /// dropped rows for an unrelated reason was the project editor (see
    /// <c>ProjectDialogViewModelTests.ToProject_Editing_KeepsEveryResourceRowUntouched</c>), which this store-level
    /// test cannot reach.
    /// </summary>
    [Fact]
    public async Task SaveAsync_AResourceNamingNoInstalledSource_RoundTripsThroughProjectStoreUnchanged()
    {
        // The host does not know or care whether "somepluginnotinstalled" names a real plugin — the same rule
        // PluginFields already follows (Project.cs, LinkedAs/PluginFields doc comment): a reference under a scheme
        // nothing here recognises is carried through unchanged rather than dropped or rewritten.
        var project = Project.Create("Cockpit") with
        {
            Resources = [new ProjectResource("somepluginnotinstalled:whatever-it-means", ProjectResourceRole.Reference)],
        };

        var store = new ProjectStore(_configFilePath);
        await store.SaveAsync(ProjectSettings.Empty.WithProject(project));
        var loaded = await store.LoadAsync();

        Assert.Equal(
            new ProjectResource("somepluginnotinstalled:whatever-it-means", ProjectResourceRole.Reference),
            Assert.Single(Assert.Single(loaded.Projects).Resources));
    }

    // --- AC4: a blank/empty Reference yields no row -----------------------------------------------------------------

    [Fact]
    public async Task SaveAsync_RowsWithABlankReference_YieldNoRowForThem()
    {
        var project = Project.Create("Cockpit") with
        {
            Resources =
            [
                new ProjectResource("", ProjectResourceRole.Memory),
                new ProjectResource("   ", ProjectResourceRole.Instructions),
                new ProjectResource("ok", ProjectResourceRole.Reference),
            ],
        };

        var store = new ProjectStore(_configFilePath);
        await store.SaveAsync(ProjectSettings.Empty.WithProject(project));
        var loaded = await store.LoadAsync();

        Assert.Equal("ok", Assert.Single(Assert.Single(loaded.Projects).Resources).Reference);
    }

    [Fact]
    public void Normalized_AProjectWithOnlyBlankResources_DropsAllOfThem()
    {
        var settings = new ProjectSettings
        {
            Projects = [Project.Create("Cockpit") with { Resources = [new ProjectResource("  ", ProjectResourceRole.Memory)] }],
        };

        Assert.Empty(settings.Normalized().Projects.Single().Resources);
    }

    // --- AC5: Label and ReachesSessions round-trip; ReachesSessions defaults to true ---------------------------------

    [Fact]
    public async Task SaveAsync_LabelAndReachesSessions_RoundTrip()
    {
        var project = Project.Create("Cockpit") with
        {
            Resources =
            [
                new ProjectResource("/home/raymond/Notes/Cockpit", ProjectResourceRole.Memory)
                {
                    Label = "Working notes",
                    ReachesSessions = false,
                },
            ],
        };

        var store = new ProjectStore(_configFilePath);
        await store.SaveAsync(ProjectSettings.Empty.WithProject(project));
        var loaded = await store.LoadAsync();

        var resource = Assert.Single(Assert.Single(loaded.Projects).Resources);
        Assert.Equal("Working notes", resource.Label);
        Assert.False(resource.ReachesSessions);
    }

    [Fact]
    public void ProjectResource_WithNoReachesSessionsSet_DefaultsToTrue() =>
        Assert.True(new ProjectResource("x", ProjectResourceRole.Memory).ReachesSessions);

    // A "true survives the round trip" test used to sit here. Removed: it could not fail. ProjectResource's own
    // default and ProjectResourceEntry's own default are both true, so the assertion would hold even if nothing in
    // between ever read or wrote the flag at all. SaveAsync_LabelAndReachesSessions_RoundTrip above carries the
    // real weight for this AC — it sets ReachesSessions = false, a value neither default would produce by
    // accident, so it actually discriminates a broken round trip from a working one.

    // --- AC6: MemoryRef mirrors the first Memory row, and is null with none ------------------------------------------

    [Fact]
    public void MemoryRef_AProjectWithNoResources_IsNull() =>
        Assert.Null(Project.Create("Cockpit").MemoryRef);

    [Fact]
    public void MemoryRef_TheFirstMemoryRowAmongOthers_IsWhatItReturns()
    {
        var project = Project.Create("Cockpit") with
        {
            Resources =
            [
                new ProjectResource("docs:readme", ProjectResourceRole.Instructions),
                new ProjectResource("depot:ai-cockpit", ProjectResourceRole.Memory),
                new ProjectResource("/home/raymond/Notes/Cockpit", ProjectResourceRole.Memory),
            ],
        };

        Assert.Equal("depot:ai-cockpit", project.MemoryRef);
    }

    [Fact]
    public void MemoryRef_SetThroughWith_AddsAMemoryRow()
    {
        var project = Project.Create("Cockpit") with { MemoryRef = "depot:ai-cockpit" };

        Assert.Equal("depot:ai-cockpit", project.MemoryRef);
        Assert.Equal(new ProjectResource("depot:ai-cockpit", ProjectResourceRole.Memory), Assert.Single(project.Resources));
    }

    [Fact]
    public void MemoryRef_SetToBlankOnAProjectWithAMemoryRow_RemovesTheRowRatherThanBlankingIt()
    {
        var project = Project.Create("Cockpit") with { MemoryRef = "depot:ai-cockpit" };

        var cleared = project with { MemoryRef = null };

        Assert.Null(cleared.MemoryRef);
        Assert.Empty(cleared.Resources);
    }

    /// <summary>
    /// AC2 lets a project keep more than one Memory row. Clearing <see cref="Project.MemoryRef"/> used to remove
    /// only the first of them, so reading it back afterwards would silently answer with the second row instead of
    /// null — <c>MemoryRef</c> is a singular name for "this project's memory", and it must not go on reporting some
    /// while claiming none.
    /// </summary>
    [Fact]
    public void MemoryRef_SetToNullWithTwoMemoryRows_RemovesBothAndLeavesOtherRolesAlone()
    {
        var reference = new ProjectResource("D:\\handbook", ProjectResourceRole.Reference);
        var project = Project.Create("Cockpit") with
        {
            Resources =
            [
                new ProjectResource("depot:ai-cockpit", ProjectResourceRole.Memory),
                new ProjectResource("/home/raymond/Notes/Cockpit", ProjectResourceRole.Memory),
                reference,
            ],
        };

        var cleared = project with { MemoryRef = null };

        Assert.Null(cleared.MemoryRef);
        Assert.Equal(new[] { reference }, cleared.Resources);
    }

    /// <summary>
    /// Two names for one place cannot both win. These pin what actually happens rather than what one would hope,
    /// because the trap is invisible at the call site: the same two assignments in the other order give a different
    /// project. Nothing sets both today; AC-485 is where the project editor would be tempted to, and this is the
    /// test that should make whoever writes it stop and pick one.
    /// </summary>
    [Fact]
    public void MemoryRefThenResources_InOneInitializer_LetsResourcesWin()
    {
        var project = Project.Create("Cockpit") with
        {
            MemoryRef = "depot:ai-cockpit",
            Resources = [new ProjectResource("D:\\Notes", ProjectResourceRole.Memory)],
        };

        Assert.Equal("D:\\Notes", project.MemoryRef);
        Assert.Single(project.Resources);
    }

    [Fact]
    public void ResourcesThenMemoryRef_InOneInitializer_LetsMemoryRefWin()
    {
        var project = Project.Create("Cockpit") with
        {
            Resources = [new ProjectResource("D:\\Notes", ProjectResourceRole.Memory)],
            MemoryRef = "depot:ai-cockpit",
        };

        Assert.Equal("depot:ai-cockpit", project.MemoryRef);
        Assert.Single(project.Resources);
    }

    // --- MUST-FIX 2: a missing or unrecognised Role never resolves to Memory, and never costs the whole file --------

    /// <summary>
    /// A row with no <c>role</c> at all — the shape a very small hand edit produces. Before this fix, a
    /// non-nullable enum with <see cref="ProjectResourceRole.Memory"/> at ordinal 0 read that as Memory: the most
    /// powerful role there is (read <em>and</em> written back to) handed to the row that never asked for it.
    /// </summary>
    [Fact]
    public async Task LoadAsync_ARowWithNoRole_LoadsAsReferenceNotMemory()
    {
        await File.WriteAllTextAsync(
            _configFilePath,
            """{"Projects":[{"Id":"p","Name":"Cockpit","Resources":[{"Reference":"D:\\handbook"}]}]}""");

        var loaded = await new ProjectStore(_configFilePath).LoadAsync();

        Assert.Equal(ProjectResourceRole.Reference, Assert.Single(Assert.Single(loaded.Projects).Resources).Role);
    }

    /// <summary>
    /// An unrecognised <c>role</c> — a typo, or a role only a newer build knows — must not throw for the whole
    /// document: before this fix, the shared <see cref="System.Text.Json.Serialization.JsonStringEnumConverter"/>
    /// failing on this one value made <c>TryReadAsync</c> return null for the entire file, which
    /// <c>CockpitConfigFileAccess</c> then treats as "the live file is damaged" — moving it aside and restoring the
    /// <c>.bak</c>, losing every other section (profiles, MCP servers, plugin storage) written since. This is the
    /// important half of the fix: proving the bad row costs only itself, not the rest of the config.
    /// </summary>
    [Fact]
    public async Task LoadAsync_ARowWithAnUnrecognisedRole_LoadsAsReferenceAndTheRestOfTheFileLoadsToo()
    {
        await File.WriteAllTextAsync(
            _configFilePath,
            """
            {
              "Projects":[
                {"Id":"p","Name":"Cockpit","Resources":[{"Reference":"D:\\handbook","Role":"Instruction"}]},
                {"Id":"q","Name":"Second project"}
              ]
            }
            """);

        var loaded = await new ProjectStore(_configFilePath).LoadAsync();

        Assert.Equal(2, System.Linq.Enumerable.Count(loaded.Projects));
        Assert.Equal(
            ProjectResourceRole.Reference,
            Assert.Single(loaded.Projects.Single(project => project.Id == "p").Resources).Role);
        Assert.Equal("Second project", loaded.Projects.Single(project => project.Id == "q").Name);
    }

    // --- FIX 7: cases the review found missing ------------------------------------------------------------------------

    /// <summary>
    /// A file written by a build in between (or hand-edited) carries both the legacy field and the list. The
    /// documented precedence — <c>Resources</c> is the fuller, more current answer — was asserted in prose
    /// (<c>ProjectEntry.ToDomain</c>'s doc comment) but never actually exercised until now.
    /// </summary>
    [Fact]
    public async Task LoadAsync_BothMemoryRefAndResourcesPresent_TrustsResources()
    {
        await File.WriteAllTextAsync(
            _configFilePath,
            """
            {"Projects":[{"Id":"p","Name":"Cockpit","MemoryRef":"depot:stale",
              "Resources":[{"Reference":"depot:current","Role":"Memory"}]}]}
            """);

        var loaded = await new ProjectStore(_configFilePath).LoadAsync();

        var project = Assert.Single(loaded.Projects);
        Assert.Equal(new ProjectResource("depot:current", ProjectResourceRole.Memory), Assert.Single(project.Resources));
        Assert.Equal("depot:current", project.MemoryRef);
    }

    /// <summary>
    /// An explicit empty list beside a legacy value is not "Resources absent" — it is a project a newer build
    /// already saved with no resources at all, and the fallback to <c>MemoryRef</c> in <c>ProjectEntry.ToDomain</c>
    /// only triggers when <c>Resources</c> is null, not when it is present-but-empty. Worth pinning apart from the
    /// "both present with rows" case above: an empty array is falsy in enough languages that "absent" is an easy
    /// mistake to make here.
    /// </summary>
    [Fact]
    public async Task LoadAsync_EmptyResourcesBesideALegacyMemoryRef_TrustsTheEmptyList()
    {
        await File.WriteAllTextAsync(
            _configFilePath,
            """{"Projects":[{"Id":"p","Name":"Cockpit","MemoryRef":"depot:stale","Resources":[]}]}""");

        var loaded = await new ProjectStore(_configFilePath).LoadAsync();

        var project = Assert.Single(loaded.Projects);
        Assert.Empty(project.Resources);
        Assert.Null(project.MemoryRef);
    }

    /// <summary>A row with a name but no location — the operator typed a label and never filled in the reference.</summary>
    [Fact]
    public async Task SaveAsync_ARowWithOnlyALabelAndABlankReference_YieldsNoRow()
    {
        var project = Project.Create("Cockpit") with
        {
            Resources = [new ProjectResource("", ProjectResourceRole.Reference) { Label = "Handbook" }],
        };

        var store = new ProjectStore(_configFilePath);
        await store.SaveAsync(ProjectSettings.Empty.WithProject(project));
        var loaded = await store.LoadAsync();

        Assert.Empty(Assert.Single(loaded.Projects).Resources);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}

using Cockpit.Core.Mcp;
using Cockpit.Core.Projects;
using Cockpit.Infrastructure.Projects;

namespace Cockpit.Core.Tests.Projects;

/// <summary>
/// Persistence of the <c>projects</c> section against a real temporary config file — the store is pointed at it
/// through its internal test constructor, so no real config directory is touched.
/// </summary>
public class ProjectStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configFilePath;

    public ProjectStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configFilePath = Path.Combine(_tempDir, "cockpit.json");
    }

    [Fact]
    public async Task LoadAsync_NoConfigFile_ReturnsNoProjects()
    {
        var projects = await new ProjectStore(_configFilePath).LoadAsync();

        Assert.Empty(projects.Projects);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsEveryField()
    {
        var store = new ProjectStore(_configFilePath);
        var project = Project.Create("Cockpit") with
        {
            Description = "The cockpit itself",
            Category = "Werk",
            SourceDirectory = "/home/raymond/RiderProjects/AI-Cockpit",
            GitUrl = "https://github.com/example/ai-cockpit.git",
            DefaultProfileLabel = "personal",
            BehaviorPrompt = "Follow the project conventions. Test before opening a PR.",
            IsolateInWorktreeByDefault = true,
            MemoryRef = "depot:ai-cockpit",
            SharedSourceName = "Depot — Work",
            LogoPath = "/home/raymond/.config/Cockpit/project-logos/abc.png",
            // Kept to the second: the overview orders on it, and a round-trip that quietly dropped it would put a
            // project the operator uses daily back among the ones they have never opened.
            LastOpenedAt = new DateTimeOffset(2026, 7, 24, 9, 30, 0, TimeSpan.FromHours(2)),
            McpOverlay = new ProjectMcpOverlay
            {
                EnabledServerNames = ["depot"],
                DisabledServerNames = ["youtrack"],
                AdditionalServers = [new McpServerConfig { Id = "id-project-tools", Name = "project-tools", Command = "uvx" }],
            },
            // In the order they were typed: it is the order the card reads them back in, and a section that came
            // back alphabetised or reversed would quietly rearrange what the operator laid out.
            AdditionalInfo =
            [
                new ProjectInfoField("Repository", "https://github.com/example/ai-cockpit") { IsSharedWithSessions = true },
                new ProjectInfoField("Customer", "Acme BV, via the service desk"),
            ],
        };

        await store.SaveAsync(ProjectSettings.Empty.WithProject(project));
        var loaded = await store.LoadAsync();

        var savedProject = Assert.Single(loaded.Projects);
        Assert.Equivalent(project, savedProject);
    }

    [Fact]
    public async Task SaveAsync_ProjectWithoutMcpChoices_RoundTripsAsTheEmptyOverlay()
    {
        var store = new ProjectStore(_configFilePath);

        await store.SaveAsync(ProjectSettings.Empty.WithProject(Project.Create("Admin")));
        var loaded = await store.LoadAsync();

        Assert.True(Assert.Single(loaded.Projects).McpOverlay.IsEmpty);
    }

    /// <summary>
    /// A credential in an information row must reach the config under the field name the secret rule recognises
    /// (AC-318) — that name is the whole mechanism by which it gets encrypted and scrubbed from backups. Written as a
    /// file assertion because the encryption itself lives above this store: what this owns is putting the value in the
    /// field that routes it there, and never in the readable one.
    /// </summary>
    [Fact]
    public async Task SaveAsync_ASecretInformationRow_GoesToTheFieldNameTheSecretRuleRecognises()
    {
        var project = Project.Create("Cockpit") with
        {
            AdditionalInfo =
            [
                new ProjectInfoField("Deploy token", "s3cr3t-value") { IsSecret = true },
                new ProjectInfoField("Repository", "https://github.com/example/repo"),
            ],
        };

        await new ProjectStore(_configFilePath).SaveAsync(ProjectSettings.Empty.WithProject(project));
        var written = await File.ReadAllTextAsync(_configFilePath);

        Assert.Contains("SecretValue", written);
        Assert.Contains("https://github.com/example/repo", written);

        var loaded = await new ProjectStore(_configFilePath).LoadAsync();
        var rows = Assert.Single(loaded.Projects).AdditionalInfo;
        Assert.True(rows[0].IsSecret, "which field carried the value is what says it is a secret");
        Assert.Equal("s3cr3t-value", rows[0].Value);
        Assert.False(rows[1].IsSecret);
    }

    /// <summary>Most projects keep no information of their own; their entry should not gain an empty array for it.</summary>
    [Fact]
    public async Task SaveAsync_ProjectWithoutInformation_WritesNoSectionForIt()
    {
        await new ProjectStore(_configFilePath).SaveAsync(ProjectSettings.Empty.WithProject(Project.Create("Admin")));

        var written = await File.ReadAllTextAsync(_configFilePath);
        Assert.DoesNotContain("AdditionalInfo", written);
    }

    /// <summary>
    /// A hand-edited information row can be half-written, and the deserializer will hand a null straight through to a
    /// property the domain declares non-nullable. Loading has to survive that with the project intact.
    /// </summary>
    [Fact]
    public async Task LoadAsync_InformationRowWithNulls_LoadsTheProjectAndDropsTheRow()
    {
        await File.WriteAllTextAsync(
            _configFilePath,
            """
            {"Projects":[{"Id":"kept","Name":"Cockpit","AdditionalInfo":[
              {"Label":null,"Value":null},
              {},
              {"Label":"Repository","Value":"https://github.com/example/repo"}]}]}
            """);

        var loaded = await new ProjectStore(_configFilePath).LoadAsync();

        var project = Assert.Single(loaded.Projects);
        Assert.Equal("kept", project.Id);
        Assert.Equal("Repository", Assert.Single(project.AdditionalInfo).Label);
    }

    /// <summary>A section written by hand, or by a newer build, should cost the operator the bad entry rather than the whole list.</summary>
    [Fact]
    public async Task LoadAsync_EntryWithoutAName_IsDropped()
    {
        await File.WriteAllTextAsync(
            _configFilePath,
            """{"Projects":[{"Id":"kept","Name":"Cockpit"},{"Id":"blank","Name":""}]}""");

        var loaded = await new ProjectStore(_configFilePath).LoadAsync();

        Assert.Equal("kept", Assert.Single(loaded.Projects).Id);
    }

    // AC-245: the per-machine "hidden shared project" flag round-trips the same as everything else in this section.

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsHiddenSharedProjectIds()
    {
        var store = new ProjectStore(_configFilePath);

        await store.SaveAsync(ProjectSettings.Empty with { HiddenSharedProjectIds = ["depot:cockpit", "depot:other"] });
        var loaded = await store.LoadAsync();

        Assert.Equal(["depot:cockpit", "depot:other"], loaded.HiddenSharedProjectIds);
    }

    [Fact]
    public async Task LoadAsync_HiddenSharedProjectIdsButNoProjects_StillLoads()
    {
        // Regression: the store's own fast path used to treat "no Projects" alone as "nothing saved at all" and
        // return ProjectSettings.Empty outright, silently dropping a hidden-ids list saved with no local projects.
        await new ProjectStore(_configFilePath).SaveAsync(ProjectSettings.Empty with { HiddenSharedProjectIds = ["depot:cockpit"] });

        var loaded = await new ProjectStore(_configFilePath).LoadAsync();

        Assert.Equal(["depot:cockpit"], loaded.HiddenSharedProjectIds);
        Assert.Empty(loaded.Projects);
    }

    [Fact]
    public async Task LoadAsync_HandEditedNullInHiddenSharedProjectIds_LoadsWithThatEntryDropped()
    {
        await File.WriteAllTextAsync(
            _configFilePath,
            """{"HiddenSharedProjectIds":[null,"depot:cockpit"]}""");

        var loaded = await new ProjectStore(_configFilePath).LoadAsync();

        Assert.Equal(["depot:cockpit"], loaded.HiddenSharedProjectIds);
    }

    // AC-618: a project's category, and the categories' own display order/casing, round-trip the same way.

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsCategoryAndCategoryOrder()
    {
        var store = new ProjectStore(_configFilePath);
        var project = Project.Create("Cockpit") with { Category = "Werk" };

        await store.SaveAsync(ProjectSettings.Empty.WithProject(project));
        var loaded = await store.LoadAsync();

        Assert.Equal("Werk", Assert.Single(loaded.Projects).Category);
        Assert.Equal(["Werk"], loaded.CategoryOrder);
    }

    /// <summary>Most projects carry no category; their own entry should not gain an empty field for it (CategoryOrder itself is always written, empty or not — that part is expected).</summary>
    [Fact]
    public async Task SaveAsync_ProjectWithoutCategory_WritesNoCategoryFieldOnTheProjectEntry()
    {
        await new ProjectStore(_configFilePath).SaveAsync(ProjectSettings.Empty.WithProject(Project.Create("Admin")));

        var written = await File.ReadAllTextAsync(_configFilePath);
        Assert.DoesNotContain("\"Category\":", written);
    }

    /// <summary>The store owns one section: writing projects must not clobber a sibling the same file carries.</summary>
    [Fact]
    public async Task SaveAsync_LeavesOtherSectionsUntouched()
    {
        await File.WriteAllTextAsync(_configFilePath, """{"Profiles":[{"Label":"personal"}]}""");

        await new ProjectStore(_configFilePath).SaveAsync(ProjectSettings.Empty.WithProject(Project.Create("Cockpit")));

        var written = await File.ReadAllTextAsync(_configFilePath);
        Assert.Contains("personal", written);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}

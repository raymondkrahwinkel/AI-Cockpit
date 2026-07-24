using FluentAssertions;
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

        projects.Projects.Should().BeEmpty("a cockpit without projects starts sessions exactly as it did before");
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsEveryField()
    {
        var store = new ProjectStore(_configFilePath);
        var project = Project.Create("Cockpit") with
        {
            Description = "The cockpit itself",
            SourceDirectory = "/home/raymond/RiderProjects/AI-Cockpit",
            GitUrl = "https://github.com/example/ai-cockpit.git",
            DefaultProfileLabel = "personal",
            BehaviorPrompt = "Follow the project conventions. Test before opening a PR.",
            IsolateInWorktreeByDefault = true,
            MemoryRef = "depot:ai-cockpit",
            McpOverlay = new ProjectMcpOverlay
            {
                DisabledServerNames = ["youtrack"],
                AdditionalServers = [new McpServerConfig { Name = "project-tools", Command = "uvx" }],
            },
        };

        await store.SaveAsync(ProjectSettings.Empty.WithProject(project));
        var loaded = await store.LoadAsync();

        loaded.Projects.Should().ContainSingle().Which.Should().BeEquivalentTo(project);
    }

    [Fact]
    public async Task SaveAsync_ProjectWithoutMcpChoices_RoundTripsAsTheEmptyOverlay()
    {
        var store = new ProjectStore(_configFilePath);

        await store.SaveAsync(ProjectSettings.Empty.WithProject(Project.Create("Admin")));
        var loaded = await store.LoadAsync();

        loaded.Projects.Should().ContainSingle().Which.McpOverlay.IsEmpty.Should().BeTrue();
    }

    /// <summary>A section written by hand, or by a newer build, should cost the operator the bad entry rather than the whole list.</summary>
    [Fact]
    public async Task LoadAsync_EntryWithoutAName_IsDropped()
    {
        await File.WriteAllTextAsync(
            _configFilePath,
            """{"Projects":[{"Id":"kept","Name":"Cockpit"},{"Id":"blank","Name":""}]}""");

        var loaded = await new ProjectStore(_configFilePath).LoadAsync();

        loaded.Projects.Should().ContainSingle().Which.Id.Should().Be("kept");
    }

    /// <summary>The store owns one section: writing projects must not clobber a sibling the same file carries.</summary>
    [Fact]
    public async Task SaveAsync_LeavesOtherSectionsUntouched()
    {
        await File.WriteAllTextAsync(_configFilePath, """{"Profiles":[{"Label":"personal"}]}""");

        await new ProjectStore(_configFilePath).SaveAsync(ProjectSettings.Empty.WithProject(Project.Create("Cockpit")));

        var written = await File.ReadAllTextAsync(_configFilePath);
        written.Should().Contain("personal");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}

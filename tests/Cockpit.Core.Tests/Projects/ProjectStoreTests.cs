using Cockpit.Core.Mcp;
using Cockpit.Core.Projects;
using Cockpit.Infrastructure.Projects;

namespace Cockpit.Core.Tests.Projects;

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
    public async Task SaveAsync_ThenLoadAsync_RoundTripsEveryField()
    {
        var store = new ProjectStore(_configFilePath);
        var project = Project.Create("Cockpit") with
        {
            Description = "The cockpit itself",
            Category = "Werk",
            SourceDirectories =
            [
                new("/home/raymond/RiderProjects/AI-Cockpit"),
                new("/home/raymond/RiderProjects/AI-Cockpit-Docs") { Label = "docs" },
            ],
            GitUrl = "https://github.com/example/ai-cockpit.git",
            DefaultProfileLabel = "personal",
            BehaviorPrompt = "Follow the project conventions. Test before opening a PR.",
            Assistant = "depot:acme/zyra@depot.acme.example",
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

    // AC-938: a cockpit.json from before this ticket only ever wrote the singular SourceDirectory field —
    // loading it must produce exactly the one-repository project it always described, and saving must keep
    // writing that same legacy field so an older build can still read the file back.

    [Fact]
    public async Task LoadAsync_LegacySourceDirectoryOnly_LoadsAsItsOneRepository()
    {
        await File.WriteAllTextAsync(
            _configFilePath,
            """{"Projects":[{"Id":"legacy","Name":"Cockpit","SourceDirectory":"/home/raymond/legacy"}]}""");

        var loaded = await new ProjectStore(_configFilePath).LoadAsync();

        var project = Assert.Single(loaded.Projects);
        Assert.Equal("/home/raymond/legacy", project.SourceDirectory);
        var repository = Assert.Single(project.SourceDirectories);
        Assert.Equal("/home/raymond/legacy", repository.Path);
        Assert.Null(repository.Label);
    }

    // AC-490 criterion 1: the id is on disk from the load that minted it, so no run points at an id a later load would replace.
    [Fact]
    public async Task AJobWrittenWithoutAnId_GetsOneOnLoadThatIsOnDiskAtOnce_AndKeepsItThroughARewrite()
    {
        // A config as AC-491 wrote it: a job with a prompt and a blast radius, and no id anywhere.
        await File.WriteAllTextAsync(_configFilePath, """
            { "Projects": [ { "Id": "p1", "Name": "Invoices", "Jobs": [
                { "Prompt": "Process this month's invoices", "BlastRadius": "changes nothing · reports only" } ] } ] }
            """);
        var store = new ProjectStore(_configFilePath);

        var loaded = Assert.Single(Assert.Single((await store.LoadAsync()).Projects).Jobs);

        // The id the load minted is already in the file — not waiting for a later save that might never come.
        Assert.False(string.IsNullOrWhiteSpace(loaded.Id));
        Assert.Contains(loaded.Id, await File.ReadAllTextAsync(_configFilePath), StringComparison.Ordinal);
        Assert.Equal(loaded.Id, Assert.Single(Assert.Single((await store.LoadAsync()).Projects).Jobs).Id);

        // Rewording the prompt is an edit to the job, not a new job: its runs stay its runs.
        var project = Assert.Single((await store.LoadAsync()).Projects);
        await store.SaveAsync(ProjectSettings.Empty.WithProject(
            project with { Jobs = [loaded with { Prompt = "Process the invoices and flag anything odd" }] }));

        var rewritten = Assert.Single(Assert.Single((await store.LoadAsync()).Projects).Jobs);
        Assert.Equal("Process the invoices and flag anything odd", rewritten.Prompt);
        Assert.Equal(loaded.Id, rewritten.Id);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}

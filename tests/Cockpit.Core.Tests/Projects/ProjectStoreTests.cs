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
            Assistant = "Zyra",
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

    // AC-318: the field name is the whole mechanism by which a credential is encrypted and scrubbed, so this asserts on the file.
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

    [Fact]
    public async Task SaveAsync_ProjectWithMultipleRepositories_StillMirrorsTheFirstIntoTheLegacyField()
    {
        var store = new ProjectStore(_configFilePath);
        var project = Project.Create("Waymark") with
        {
            SourceDirectories = [new("/home/raymond/waymark-web"), new("/home/raymond/waymark-android") { Label = "android" }],
        };

        await store.SaveAsync(ProjectSettings.Empty.WithProject(project));

        var written = await File.ReadAllTextAsync(_configFilePath);
        Assert.Contains("\"SourceDirectory\"", written, StringComparison.Ordinal);
        Assert.Contains("/home/raymond/waymark-web", written, StringComparison.Ordinal);

        var loaded = await store.LoadAsync();
        var reloaded = Assert.Single(loaded.Projects);
        Assert.Equal(2, reloaded.SourceDirectories.Count);
        Assert.Equal("android", reloaded.SourceDirectories[1].Label);
    }

    // AC-245: the per-machine "hidden shared project" flag round-trips the same as everything else in this section.

    // AC-618: a project's category, and the categories' own display order/casing, round-trip the same way.

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

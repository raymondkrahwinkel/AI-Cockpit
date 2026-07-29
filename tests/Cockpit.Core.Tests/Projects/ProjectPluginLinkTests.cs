using Cockpit.Core.Projects;
using Cockpit.Infrastructure.Projects;

namespace Cockpit.Core.Tests.Projects;

/// <summary>
/// What a project is called elsewhere (AC-317): the link a plugin resolves, how it is normalized, and that it
/// survives a round trip through <c>cockpit.json</c> — including under a key belonging to a plugin that is not
/// installed, which is the case that decides whether uninstalling a plugin unlinks every project that used it.
/// </summary>
public class ProjectPluginLinkTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configFilePath;

    public ProjectPluginLinkTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configFilePath = Path.Combine(_tempDir, "cockpit.json");
    }

    private static Project Linked(params (string Key, string Value)[] links) =>
        Project.Create("Cockpit") with
        {
            PluginFields = links.ToDictionary(link => link.Key, link => link.Value, StringComparer.Ordinal),
        };

    [Fact]
    public void LinkedAs_AKeyNothingLinked_IsNull()
    {
        Assert.Null(Linked(("youtrack.project", "AC")).LinkedAs("github.repository"));
    }

    [Fact]
    public void LinkedAs_MatchesTheKeyExactly()
    {
        // Plugin ids and intent actions are matched case-sensitively, and a link must answer the same way: a plugin
        // that asks for "youtrack.project" and gets what was stored under "YouTrack.Project" would be querying a
        // tracker with an identifier nobody meant to give it.
        var project = Linked(("youtrack.project", "AC"));

        Assert.Equal("AC", project.LinkedAs("youtrack.project"));
        Assert.Null(project.LinkedAs("YouTrack.Project"));
    }

    [Fact]
    public void LinkedAs_AKeyStoredWithABlankValue_IsNull()
    {
        Assert.Null(Linked(("youtrack.project", "   ")).LinkedAs("youtrack.project"));
    }

    [Fact]
    public void Normalized_DropsALinkThatNamesNothingAndTrimsTheRest()
    {
        var settings = new ProjectSettings
        {
            Projects = [Linked(("youtrack.project", "  AC  "), ("github.repository", "  "))],
        };

        var links = settings.Normalized().Projects.Single().PluginFields;

        Assert.Equal(new KeyValuePair<string, string>("youtrack.project", "AC"), Assert.Single(links));
    }

    [Fact]
    public void Normalized_NothingToTidy_HandsBackTheSameProjects()
    {
        // The same reference, not merely equal content: a record compares a dictionary by reference, so rebuilding it
        // on every load would make the caller's SequenceEqual false forever and rebuild the whole list each time.
        var settings = new ProjectSettings { Projects = [Linked(("youtrack.project", "AC"))] };

        Assert.Same(settings.Projects, settings.Normalized().Projects);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_KeepsALinkWhoseKeyNoPluginClaims()
    {
        // A plugin that is uninstalled — or simply not on this machine — must not cost a project its link. The store
        // knows nothing about which keys are live, which is exactly why this survives.
        var store = new ProjectStore(_configFilePath);
        await store.SaveAsync(new ProjectSettings
        {
            Projects = [Linked(("youtrack.project", "AC"), ("depot.project", "ai-cockpit"))],
        });

        var loaded = await new ProjectStore(_configFilePath).LoadAsync();

        Assert.Equivalent(
            new Dictionary<string, string>
            {
                ["youtrack.project"] = "AC",
                ["depot.project"] = "ai-cockpit",
            },
            loaded.Projects.Single().PluginFields);
    }

    [Fact]
    public async Task LoadAsync_ALinkedProject_LooksItUpTheSameWayItWasStored()
    {
        // End to end rather than on the record alone: a link is written by one layer, read by another, and looked up
        // by a third. Loosen the comparer anywhere along that path and a plugin starts resolving an identifier
        // nobody meant to give it.
        var store = new ProjectStore(_configFilePath);
        await store.SaveAsync(new ProjectSettings { Projects = [Linked(("youtrack.project", "AC"))] });

        var loaded = await new ProjectStore(_configFilePath).LoadAsync();

        Assert.Equal("AC", loaded.Projects.Single().LinkedAs("youtrack.project"));
        Assert.Null(loaded.Projects.Single().LinkedAs("YOUTRACK.PROJECT"));
    }

    [Fact]
    public async Task SaveAsync_AnUnlinkedProject_WritesNoSectionForIt()
    {
        var store = new ProjectStore(_configFilePath);
        await store.SaveAsync(new ProjectSettings { Projects = [Project.Create("Cockpit")] });

        var json = await File.ReadAllTextAsync(_configFilePath);

        Assert.DoesNotContain("pluginFields", json);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}

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

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}

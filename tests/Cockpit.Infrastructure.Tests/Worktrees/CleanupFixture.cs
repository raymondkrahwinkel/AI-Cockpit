using System.Text.Json;
using Cockpit.Infrastructure.Worktrees;

namespace Cockpit.TestSupport;

internal sealed class CleanupFixture : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("ac1537-").FullName;
    public string Repository => Path.Combine(Root, "repo");
    public string TempRoot => Path.Combine(Root, "temp", "cockpit");
    public WorktreeRegistryStore Registry { get; }
    public CleanupDocker Docker { get; } = new();
    public WorktreeManager Manager { get; }

    public CleanupFixture()
    {
        Registry = new WorktreeRegistryStore(Path.Combine(Root, "cockpit.json"));
        Manager = new WorktreeManager(Registry, Path.Combine(Root, "worktrees"), dockerCli: Docker, sessionTempRoot: TempRoot);
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Repository);
        await GitAsync(Repository, "init", "-b", "main");
        await GitAsync(Repository, "config", "user.email", "test@example.com");
        await GitAsync(Repository, "config", "user.name", "Test");
        await File.WriteAllTextAsync(Path.Combine(Repository, "file.txt"), "base\n");
        await GitAsync(Repository, "add", ".");
        await GitAsync(Repository, "commit", "-m", "base");
        var remote = Path.Combine(Root, "remote.git");
        await GitAsync(Root, "init", "--bare", remote);
        await GitAsync(Repository, "remote", "add", "origin", remote);
        await GitAsync(Repository, "push", "-u", "origin", "main");
    }

    public static Task<string> GitAsync(string directory, params string[] arguments) =>
        GitCli.RunCheckedAsync(directory, arguments, CancellationToken.None);

    public void Dispose()
    {
        Manager.Dispose();
        // Git objects can be read-only on Windows.
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(Root, recursive: true);
    }
}

internal sealed class CleanupDocker : IDockerCli
{
    private readonly List<object> _containers = [];
    public HashSet<string> Removed { get; } = [];

    public void Add(string id, string image, Dictionary<string, string> labels) =>
        _containers.Add(new { Id = id, Config = new { Image = image, Labels = labels }, State = new { Running = true } });

    public Task<DockerCliResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        if (arguments.SequenceEqual(new[] { "ps", "-aq" }))
        {
            return Task.FromResult(new DockerCliResult(0, _containers.Count > 0 ? "container" : "", ""));
        }

        if (arguments[0] == "inspect")
        {
            return Task.FromResult(new DockerCliResult(0, JsonSerializer.Serialize(_containers), ""));
        }

        if (arguments[0] == "rm")
        {
            Removed.UnionWith(arguments.Skip(2));
        }

        return Task.FromResult(new DockerCliResult(0, "", ""));
    }
}

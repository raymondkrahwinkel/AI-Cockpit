using System.Text.Json;
using Cockpit.Core.Abstractions;
using Cockpit.Core.HeavyRuns;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Plugins.Abstractions.HeavyRuns;

namespace Cockpit.Infrastructure.HeavyRuns;

// AC-1535: the peak each kind of heavy run added to the machine, per repository, kept across restarts so the queue
// admits on a measured number instead of a guess. The latest run's measurement replaces the one before it.
public sealed class HeavyRunCostStore : ISingletonService
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writing = new(1, 1);
    private Dictionary<string, HeavyRunCost>? _costs;

    public HeavyRunCost? Get(string key)
    {
        lock (_gate)
        {
            return _Costs().GetValueOrDefault(key);
        }
    }

    public async Task RecordAsync(string key, HeavyRunCost cost)
    {
        string json;
        lock (_gate)
        {
            _Costs()[key] = cost;
            json = JsonSerializer.Serialize(_Costs());
        }

        await _writing.WaitAsync().ConfigureAwait(false);
        try
        {
            await File.WriteAllTextAsync(CockpitConfigPath.HeavyRunCosts, json).ConfigureAwait(false);
        }
        finally
        {
            _writing.Release();
        }
    }

    // A worktree shares its repository's costs: its `.git` file points into the checkout's `.git/worktrees/<name>`.
    public static string Key(HeavyRunKind kind, string workingDirectory)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(workingDirectory)); directory is not null; directory = directory.Parent)
        {
            var git = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(git))
            {
                return $"{kind}|{directory.FullName}";
            }

            if (File.Exists(git) && _ReadPointer(git) is { } pointer && pointer.StartsWith("gitdir:", StringComparison.Ordinal))
            {
                var worktreeGitDir = new DirectoryInfo(pointer["gitdir:".Length..].Trim());
                return $"{kind}|{worktreeGitDir.Parent?.Parent?.Parent?.FullName ?? directory.FullName}";
            }
        }

        return $"{kind}|{Path.GetFullPath(workingDirectory)}";
    }

    private static string? _ReadPointer(string gitFile)
    {
        try
        {
            return File.ReadAllText(gitFile).Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private Dictionary<string, HeavyRunCost> _Costs()
    {
        if (_costs is not null)
        {
            return _costs;
        }

        try
        {
            _costs = File.Exists(CockpitConfigPath.HeavyRunCosts)
                ? JsonSerializer.Deserialize<Dictionary<string, HeavyRunCost>>(File.ReadAllText(CockpitConfigPath.HeavyRunCosts))
                : null;
        }
        catch (JsonException)
        {
            // A damaged file costs only what was learned; the next runs measure it again.
            _costs = null;
        }

        return _costs ??= new Dictionary<string, HeavyRunCost>(StringComparer.Ordinal);
    }
}

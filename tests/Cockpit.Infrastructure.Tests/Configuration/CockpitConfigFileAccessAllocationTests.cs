using System.Text.Json;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Tests.Configuration;

/// <summary>
/// AC-1152, and the test nobody had: how large <c>cockpit.json</c> may grow before the way it is read and
/// written stops fitting. A read-modify-write of the whole document is fine at 20 kB and a large-object-heap
/// generator at 175 kB — anything over 85 kB is allocated straight on the LOH, and <c>AllocLarge</c> was the
/// measured reason for four of five gen2 collections. Nothing went red when the file crossed that line, so
/// this is the line: not the file's size, which the plugins decide, but what one round trip over it costs.
/// </summary>
// The counter this reads is process-wide, so the rest of the assembly must not be allocating alongside it.
[CollectionDefinition(nameof(AllocationBudget), DisableParallelization = true)]
public sealed class AllocationBudget;

[Collection(nameof(AllocationBudget))]
public class CockpitConfigFileAccessAllocationTests : IDisposable
{
    // The operator's own file measured 175.343 bytes on 2026-08-29, so the fixture is built to that size and
    // that shape. A budget stated against a toy config would say nothing about the file this is about.
    private const int RealisticConfigBytes = 175_000;

    // What one read plus one write of that config may allocate. Measured on 2026-08-29: 3.488.168 bytes before
    // AC-1152 — nineteen times the file, because each direction materialised the whole document as one string and
    // the secret walker rebuilt every plugin's cache into a node tree on top of that — and 1.966.792 after. The
    // budget sits between the two with room to move: this guards the shape of the route, not a benchmark score.
    private const long RoundBudgetBytes = 2_500_000;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"cockpit-config-alloc-{Guid.NewGuid():N}");

    private string ConfigPath => Path.Combine(_directory, "cockpit.json");

    public CockpitConfigFileAccessAllocationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Measure_WhenTheWorkAllocatesALargeObject_ReportsIt()
    {
        // The positive control. `Measure` takes the smallest of several rounds off a process-wide counter, so a
        // low reading could as easily mean a blind instrument as a cheap round; this is the case that must read high.
        var allocated = Measure(() => GC.KeepAlive(new byte[1_000_000]));

        Assert.True(allocated >= 1_000_000, $"the instrument reported {allocated} bytes for a 1 MB allocation");
    }

    [Fact]
    public async Task ReadAndWrite_OfARealisticConfig_StaysWithinItsBudget()
    {
        var access = new CockpitConfigFileAccess(ConfigPath);
        await access.UpdateNowAsync(FillToRealisticSize, CancellationToken.None);

        var onDisk = new FileInfo(ConfigPath).Length;
        Assert.True(onDisk > RealisticConfigBytes * 0.9, $"the fixture is {onDisk} bytes, no longer a realistic config");

        var allocated = Measure(() =>
        {
            var configFile = access.ReadNowAsync(CancellationToken.None).GetAwaiter().GetResult()!;
            access.WriteNowAsync(configFile).GetAwaiter().GetResult();
        });

        Assert.True(
            allocated < RoundBudgetBytes,
            $"one read and one write of a {onDisk} byte config allocated {allocated} bytes, over the {RoundBudgetBytes} budget");
    }

    // Smallest of several rounds off the process-wide counter: the rest of the assembly runs alongside and only
    // ever adds to it, so the minimum is the tightest honest upper bound on what `work` itself costs.
    private static long Measure(Action work)
    {
        work();

        var lowest = long.MaxValue;
        for (var round = 0; round < 5; round++)
        {
            var before = GC.GetTotalAllocatedBytes(precise: true);
            work();
            lowest = Math.Min(lowest, GC.GetTotalAllocatedBytes(precise: true) - before);
        }

        return lowest;
    }

    // The shape the operator's file had on 2026-08-29, section by section: two plugin caches holding JSON inside
    // a JSON string are half of it, a long tail of small plugin sections and ordinary settings the rest. The
    // caches carry no credential and the plugins that hold one do, which is how the real file is arranged.
    private static void FillToRealisticSize(CockpitConfigFile config)
    {
        config.Plugins["workflows"] = Plugin(("runs", 150), ("workflows", 6));
        config.Plugins["github-pull-requests"] = Plugin(("refreshSourceSnapshot", 68), ("cachedPullRequests", 10));
        config.Plugins["youtrack"] = Plugin(("template", 9));
        config.Plugins["autopilot"] = Plugin(("runHistory", 8));

        foreach (var index in Enumerable.Range(0, 30))
        {
            config.Plugins[$"plugin-{index}"] = Plugin(($"settings-{index}", 1));
        }

        // A credential stored flat, and one buried in a plugin's own JSON — the two shapes the walker exists for,
        // so the budget covers a round that has to find them rather than one that never looks.
        config.Plugins["slack"] = new PluginRegistrationEntry { Data = { ["assistantChannel.botToken"] = "xoxb-fixture" } };
        config.Plugins["kubernetes"] = new PluginRegistrationEntry { Data = { ["clusters"] = """[{"name":"one","token":"fixture"}]""" } };

        config.Profiles =
        [
            .. Enumerable.Range(0, 42).Select(index => SessionProfileEntry.FromDomain(
                new SessionProfile($"profile-{index}", new ClaudeConfig($"/home/someone/.claude-{index}"), Purpose: new string('x', 300)))),
        ];
    }

    private static PluginRegistrationEntry Plugin(params (string Key, int Records)[] data)
    {
        var entry = new PluginRegistrationEntry { Enabled = true };
        foreach (var (key, records) in data)
        {
            entry.Data[key] = Records(key, records);
        }

        return entry;
    }

    // A plugin's cache: its own JSON, serialised into a string, the way plugin storage holds it.
    private static string Records(string kind, int count)
    {
        var records = Enumerable.Range(0, count).Select(index => new Dictionary<string, string>
        {
            ["Id"] = $"{kind}-{index}",
            ["Title"] = $"{kind} number {index}",
            ["Body"] = new string('x', 300),
            ["UpdatedAt"] = "2026-08-29T14:00:00Z",
        });

        return JsonSerializer.Serialize(records);
    }
}

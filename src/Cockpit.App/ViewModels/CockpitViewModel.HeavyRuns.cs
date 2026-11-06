using Cockpit.Core.HeavyRuns;
using Cockpit.Infrastructure.HeavyRuns;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cockpit.App.ViewModels;

public partial class CockpitViewModel
{
    private readonly IHeavyRunSettingsStore? _heavyRunSettingsStore;
    private readonly HeavyRunQueue? _heavyRunQueue;

    [ObservableProperty]
    private int _maxParallelRuns;

    [ObservableProperty]
    private double _runCpuCeilingPercent = new HeavyRunSettings().CpuCeilingPercent;

    [ObservableProperty]
    private double _runMemoryCeilingPercent = new HeavyRunSettings().MemoryCeilingPercent;

    private async Task LoadHeavyRunSettingsAsync()
    {
        if (_heavyRunSettingsStore is null)
        {
            return;
        }

        var settings = await _heavyRunSettingsStore.LoadAsync();
        MaxParallelRuns = settings.MaxParallelRuns;
        RunCpuCeilingPercent = settings.CpuCeilingPercent;
        RunMemoryCeilingPercent = settings.MemoryCeilingPercent;
    }

    private async Task SaveHeavyRunSettingsAsync()
    {
        if (_heavyRunSettingsStore is null)
        {
            return;
        }

        // Preserve the queue's other settings, including its disk limits.
        var settings = await _heavyRunSettingsStore.LoadAsync();
        await _heavyRunSettingsStore.SaveAsync(settings with
        {
            MaxParallelRuns = MaxParallelRuns,
            CpuCeilingPercent = RunCpuCeilingPercent,
            MemoryCeilingPercent = RunMemoryCeilingPercent,
        });
    }
}

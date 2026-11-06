using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Shell;

namespace Cockpit.App.ViewModels;

// AC-2028: the status bar's "Test runs: N" and the dialog behind it, over `ITestRuns` — tracked and heavy runs as one list.
public sealed partial class TestRunsViewModel : ObservableObject, ISingletonService
{
    private readonly ITestRuns? _runs;

    public TestRunsViewModel()
    {
        Apply(
        [
            new TestRunEntry("run-7f3a91", "dotnet test Cockpit.slnx", TestRunState.Running, false, "light", null, @"D:\Projects\dotnet\Cockpit", "Running for 2m 14s", string.Empty, () => Task.CompletedTask),
            new TestRunEntry("run-2c81d0", "docker compose build", TestRunState.Queued, true, "build", null, @"D:\Projects\Depot", "waiting for 6 GiB RAM", string.Empty, () => Task.CompletedTask),
            new TestRunEntry("run-91be44", "npm test", TestRunState.Passed, false, null, null, null, "Exit code 0, 48s", "All 212 tests passed.", null),
        ]);
    }

    public TestRunsViewModel(ITestRuns runs)
    {
        _runs = runs;
        _runs.Changed += () => Dispatcher.UIThread.Post(Refresh);
        Refresh();
    }

    // Names the session behind a pane id; set by the cockpit once it has its sessions.
    public Func<string, string?>? SessionName { get; set; }

    public ObservableCollection<TestRunGroupViewModel> Groups { get; } = [];

    // Running plus queued, tracked and heavy together — the number on the status bar.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActive))]
    private int _activeCount;

    public bool HasActive => ActiveCount > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private TestRunRowViewModel? _selectedRun;

    public bool HasSelection => SelectedRun is not null;

    [RelayCommand]
    public void Refresh()
    {
        if (_runs is not null)
        {
            Apply(_runs.Entries());
        }
    }

    [RelayCommand]
    private async Task StopAsync(TestRunRowViewModel? run)
    {
        if (run?.Entry.StopAsync is { } stop && run.CanStop)
        {
            await stop();
            Refresh();
        }
    }

    internal void Apply(IReadOnlyList<TestRunEntry> entries)
    {
        var selectedId = SelectedRun?.RunId;
        var rows = entries.Select(entry => new TestRunRowViewModel(entry, entry.Session is { } pane ? SessionName?.Invoke(pane) : null)).ToList();
        Groups.Clear();
        foreach (var (title, state) in new[] { ("Running", TestRunState.Running), ("Queued", TestRunState.Queued) })
        {
            _AddGroup(title, rows.Where(row => row.Entry.State == state).ToList());
        }

        _AddGroup("Finished", rows.Where(row => !row.Entry.IsActive).ToList());
        ActiveCount = entries.Count(entry => entry.IsActive);
        SelectedRun = rows.FirstOrDefault(row => row.RunId == selectedId) ?? rows.FirstOrDefault();
    }

    private void _AddGroup(string title, IReadOnlyList<TestRunRowViewModel> rows)
    {
        if (rows.Count > 0)
        {
            Groups.Add(new TestRunGroupViewModel(title, rows));
        }
    }
}

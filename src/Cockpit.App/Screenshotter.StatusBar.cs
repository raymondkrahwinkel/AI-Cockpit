using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Shell;
using Cockpit.Core.Diagnostics;
using Cockpit.Core.HeavyRuns;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App;

// AC-2028: the status bar at rest and busy, the test-runs dialog and the disk panel.
internal static partial class Screenshotter
{
    private static void _HoverStatusMeters(Window window)
    {
        _UseDisks(window, critical: false, open: false);
        var buttons = window.GetVisualDescendants().OfType<Button>();
        var disk = buttons.OfType<DiskStorageStatus>().Single();
        var cockpit = (CockpitViewModel)(window.DataContext ?? throw new InvalidOperationException("Status bar scene has no view model."));
        var cpu = buttons.Single(button => button.Command == cockpit.ToggleResourcePanelCommand);
        ((IPseudoClasses)disk.Classes).Set(":pointerover", true);
        ((IPseudoClasses)cpu.Classes).Set(":pointerover", true);
        Dispatcher.UIThread.RunJobs();
    }

    private static MainWindow _StatusBarScene(bool busy)
    {
        var cockpit = new CockpitViewModel();
        cockpit.DelegatedTasks.Tasks.Clear();
        cockpit.DelegatedTasks.Groups.Clear();
        cockpit.TestRuns.Apply([]);
        cockpit.ResourceCpu = "CPU 38%  ·  RAM ";
        cockpit.ResourceMemory = "2.910 MB";
        cockpit.ResourceSessions = "3 sessions";
        cockpit.Worktrees.Count = 0;
        if (busy)
        {
            cockpit.Worktrees.Count = 1;
            cockpit.TestRuns.Apply(_SampleRuns());
            cockpit.SetPluginStatusText("depot", "● 1 live note · Depot", SideMenuButtonBadgeTone.Success, _ => new TextBlock());
        }

        return new MainWindow { DataContext = cockpit };
    }

    private static TestRunsDialog _TestRunsScene()
    {
        var viewModel = new TestRunsViewModel
        {
            SessionName = pane => pane switch
            {
                "pane-1912" => "AC-1912 statusbar opruimen",
                "pane-1905" => "AC-1905 screenshot baselines",
                "pane-311" => "AC-311 Depot compose",
                _ => null,
            },
        };
        viewModel.Apply(_SampleRuns());
        return new TestRunsDialog { DataContext = viewModel };
    }

    private static IReadOnlyList<TestRunEntry> _SampleRuns() =>
    [
        new("run-7f3a91", "dotnet test Cockpit.slnx", TestRunState.Running, false, "light", "pane-1912", @"D:\Projects\dotnet\Cockpit", "2m 14s", string.Empty, () => Task.CompletedTask),
        new("run-5be210", "Screenshotter baselines", TestRunState.Running, true, "journeys", "pane-1905", @"D:\Projects\dotnet\Cockpit", "6m 02s", string.Empty, () => Task.CompletedTask),
        new("run-2c81d0", "docker compose build", TestRunState.Queued, true, "build", "pane-311", @"D:\Projects\Depot", "waiting for 6 GiB RAM", string.Empty, () => Task.CompletedTask),
        new("run-91be44", "npm test", TestRunState.Passed, false, null, "pane-309", null, "Exit code 0, 48s", "All 212 tests passed.", null),
        new("run-04c7f2", "dotnet test Cockpit.Infrastructure.Tests", TestRunState.Failed, false, null, null, null, "Exit code 1, 190s", "Failed Cockpit.Infrastructure.Tests.SomeTest\n  Expected: 3\n  Actual:   2", null),
        new("run-ab5510", "pytest -k ingest", TestRunState.Stopped, false, null, null, null, "Exit code -1, 12s", string.Empty, null),
    ];

    // The disks the bar and the panel are drawn from: `critical` is the C: drive with a forecast, plus G: under pressure.
    private static void _UseDisks(Window window, bool critical, bool open)
    {
        var status = window.GetVisualDescendants().OfType<DiskStorageStatus>().First();
        const long gib = 1024L * 1024 * 1024;
        IReadOnlyList<DiskSpaceSnapshot> disks = critical
            ?
            [
                new("C:\\", 476 * gib, (long)(3.2 * gib), TimeSpan.FromMinutes(48), DiskPressure.Critical),
                new("G:\\", 1000 * gib, 120 * gib, TimeSpan.FromHours(7), DiskPressure.Elevated),
                new("D:\\", 931 * gib, 640 * gib, null, DiskPressure.Normal),
            ]
            :
            [
                new("C:\\", 476 * gib, 212 * gib, null, DiskPressure.Normal),
                new("G:\\", 1000 * gib, (long)(8.1 * gib), null, DiskPressure.Normal),
            ];
        status.Use(new SceneResources(disks), new SceneDocker());
        if (open)
        {
            status.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }

        Dispatcher.UIThread.RunJobs();
    }

    private sealed class SceneResources(IReadOnlyList<DiskSpaceSnapshot> disks,
        IReadOnlyDictionary<string, (string Path, long? Bytes)>? scratchUsage = null) : IMachineResources
    {
        public MachineLoad Current => MachineLoad.Unknown;

        public IReadOnlyList<DiskSpaceSnapshot> Disks => disks;

        public IReadOnlyDictionary<string, (string Path, long? Bytes)> ScratchUsage => scratchUsage
            ?? System.Collections.ObjectModel.ReadOnlyDictionary<string, (string Path, long? Bytes)>.Empty;

        public long? FreeDiskBytes(string directory) => null;

        public event Action? Sampled { add { } remove { } }
    }

    private sealed class SceneDocker : IDockerStorageMaintenance
    {
        public string Summary => "Docker: 18.4 GiB reclaimable (build cache 12.1 GiB, images 6.3 GiB)";

        public IReadOnlyList<string> History => ["Cleaned up 4.2 GiB at 09:12"];

        public bool CanCompact => true;

        public event Action? Changed { add { } remove { } }

        public void Start()
        {
        }

        public Task CleanupAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RequestCompactionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

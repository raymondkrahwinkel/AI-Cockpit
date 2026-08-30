using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.App.Services;
using Cockpit.Core.Configuration;
using Cockpit.Core.Diagnostics;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.App.ViewModels;

// The copy is the point: the tester sends us that text instead of screenshots of Activity Monitor and a hunt through
// crash-report folders, which is exactly what AC-57 could not get (AC-58).
public sealed partial class DiagnosticsViewModel(
    DiagnosticsCollector? collector,
    Func<IReadOnlyList<SessionDescriptor>> sessions) : ObservableObject
{
    [ObservableProperty]
    private string _report = "Refresh to read the current diagnostics.";

    [ObservableProperty]
    private string? _status;

    public void Refresh()
    {
        Status = null;

        // Without a collector (the design-time previewer, the screenshotter) the panel still shows the sections that
        // read only this process — platform, rendering, memory — and simply reports no sessions or crash logs.
        Report = _Format(collector?.Collect(sessions()) ?? DiagnosticsCollector.SelfReadSnapshot());
    }

    public void MarkCopied() => Status = "Copied to clipboard.";

    private static string _Format(DiagnosticsSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"{CockpitProduct.DisplayName} diagnostics — {snapshot.CapturedAt:yyyy-MM-dd HH:mm:ss}");

        var platform = snapshot.Platform;
        builder.AppendLine().AppendLine("Platform");
        builder.AppendLine($"  OS            : {platform.OperatingSystem}");
        builder.AppendLine($"  Architecture  : {platform.OsArchitecture} (process {platform.ProcessArchitecture})");
        builder.AppendLine($"  Runtime       : {platform.RuntimeVersion}");
        builder.AppendLine($"  Avalonia      : {platform.AvaloniaVersion}");
        builder.AppendLine($"  App           : {platform.AppVersion}");

        builder.AppendLine().AppendLine("Rendering");
        builder.AppendLine($"  Mode          : {snapshot.Rendering.Mode}");
        builder.AppendLine($"  {snapshot.Rendering.Detail}");

        var memory = snapshot.Memory;
        builder.AppendLine().AppendLine("Memory (process)");
        builder.AppendLine($"  Resident      : {ByteSize.Human(memory.ResidentBytes)}   ← physical memory in use (the figure that matters)");
        builder.AppendLine($"  Peak resident : {ByteSize.Human(memory.PeakResidentBytes)}");
        builder.AppendLine($"  Virtual       : {ByteSize.Human(memory.VirtualBytes)}   ← reserved address space, not usage (large is normal for .NET)");
        builder.AppendLine($"  Private       : {(memory.PrivateBytes is { } priv ? ByteSize.Human(priv) : "n/a on this platform")}");
        builder.AppendLine($"  Swap          : {(memory.SwapBytes is { } swap ? ByteSize.Human(swap) : "n/a on this platform")}");
        builder.AppendLine($"  Machine total : {ByteSize.Human(snapshot.MachineMemoryBytes)}");

        var heap = snapshot.ManagedHeap;
        builder.AppendLine().AppendLine("Managed heap");
        builder.AppendLine($"  GC mode       : {(heap.IsServerGc ? "Server" : "Workstation")}");
        builder.AppendLine($"  Heap size     : {ByteSize.Human(heap.HeapSizeBytes)}");
        builder.AppendLine($"  In use        : {ByteSize.Human(heap.InUseManagedBytes)}   ← occupancy incl. uncollected garbage, not retention");
        builder.AppendLine($"  Allocated     : {ByteSize.Human(heap.TotalAllocatedBytes)} (total since start)");
        builder.AppendLine($"  Collections   : gen0 {heap.Gen0Collections} · gen1 {heap.Gen1Collections} · gen2 {heap.Gen2Collections}");

        builder.AppendLine().AppendLine($"Sessions ({snapshot.Sessions.Count})");
        if (snapshot.Sessions.Count == 0)
        {
            builder.AppendLine("  none open");
        }
        else
        {
            foreach (var session in snapshot.Sessions)
            {
                var process = session.ProcessId is { } pid ? $"pid {pid} · {ByteSize.Human(session.ResidentBytes)}" : "no local process";
                builder.AppendLine($"  - {session.Title} [{session.Kind}] · {process}");
            }
        }

        // Named here because the cockpit tells the operator to "see the log" in several places and until now never
        // said where that is — a referral to a file the UI does not name is barely a referral at all.
        builder.AppendLine().AppendLine("Cockpit log");
        builder.AppendLine($"  {CockpitBuild.LogPath}");
        // AC-718: the log is truncated to this path on every start, so after a freeze the interesting tail is a
        // generation back. AC-1113: three of them are kept, so two quick restarts no longer lose the freeze.
        for (var generation = 1; generation <= CredentialFileHousekeeping.KeptLogGenerations; generation++)
        {
            var restarts = generation == 1 ? "the last restart" : $"{generation} restarts ago";
            builder.AppendLine($"  Previous run {generation}: {CredentialFileHousekeeping.KeptLogPath(CockpitBuild.LogPath, generation)}   ← the tail from before {restarts} (a freeze/crash is usually here, not in the live log)");
        }

        builder.AppendLine().AppendLine("Crash / memory logs (newest first)");
        if (snapshot.CrashLogs.Count == 0)
        {
            builder.AppendLine("  none found");
        }
        else
        {
            foreach (var entry in snapshot.CrashLogs)
            {
                var when = entry.Timestamp is { } timestamp ? timestamp.ToString("yyyy-MM-dd HH:mm") : "time unknown";
                builder.AppendLine($"  - [{entry.Source}] {when} · {entry.Summary}");
                if (entry.Path.Length > 0)
                {
                    builder.AppendLine($"      {entry.Path}");
                }
            }
        }

        return builder.ToString();
    }
}

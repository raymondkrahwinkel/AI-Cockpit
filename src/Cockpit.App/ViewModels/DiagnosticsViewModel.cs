using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.App.Services;
using Cockpit.Core.Diagnostics;

namespace Cockpit.App.ViewModels;

/// <summary>
/// The Debug tab's diagnostics panel (AC-58): the app reporting what it runs on, how it draws, what it holds in
/// memory, the sessions open, and the crash artifacts the OS left — as one block of monospace text the tester can
/// read and copy. The copy is the point: the tester sends us that text instead of screenshots of Activity Monitor
/// and a hunt through crash-report folders, which is exactly what AC-57 could not get.
/// <para>
/// The report is rendered on demand and when the dialog opens, never on a timer — a diagnostics page nobody is
/// looking at should cost nothing. A second refresh after leaving it open shows whether the managed heap or the
/// gen2 count is climbing, the tell for the un-disposed-subscription leak AC-57 turned to once Metal was ruled out.
/// </para>
/// </summary>
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
        builder.AppendLine($"AI-Cockpit diagnostics — {snapshot.CapturedAt:yyyy-MM-dd HH:mm:ss}");

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
        builder.AppendLine($"  Private       : {ByteSize.Human(memory.PrivateBytes)}");
        builder.AppendLine($"  Swap          : {(memory.SwapBytes is { } swap ? ByteSize.Human(swap) : "n/a on this platform")}");
        builder.AppendLine($"  Machine total : {ByteSize.Human(snapshot.MachineMemoryBytes)}");

        var heap = snapshot.ManagedHeap;
        builder.AppendLine().AppendLine("Managed heap");
        builder.AppendLine($"  GC mode       : {(heap.IsServerGc ? "Server" : "Workstation")}");
        builder.AppendLine($"  Heap size     : {ByteSize.Human(heap.HeapSizeBytes)}");
        builder.AppendLine($"  Live          : {ByteSize.Human(heap.LiveManagedBytes)}");
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

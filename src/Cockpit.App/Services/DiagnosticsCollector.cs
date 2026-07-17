using System.Reflection;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Diagnostics;
using Cockpit.Core.Diagnostics;

namespace Cockpit.App.Services;

/// <summary>
/// Assembles the diagnostics snapshot the Debug tab shows and the tester copies (AC-58). It lives in the App layer
/// because that is the only one that can name the render backend and the toolkit version; the memory and process
/// figures reuse the same readers the resource monitor already uses (#78), so the panel and the status bar cannot
/// disagree about what a session weighs.
/// </summary>
public sealed class DiagnosticsCollector(IProcessTableReader processTable, ICrashLogReader crashLogReader) : ISingletonService
{
    private const int MaxCrashEntries = 3;

    /// <summary>
    /// Reads the machine once and builds a full snapshot for <paramref name="sessions"/>. A session with a process
    /// is weighed as its whole tree (the <c>claude</c> process plus what it spawned), the same figure the status
    /// bar reports; one with no process contributes nothing local to weigh.
    /// </summary>
    public DiagnosticsSnapshot Collect(IReadOnlyList<SessionDescriptor> sessions)
    {
        var rows = processTable.Read();

        var sessionDiagnostics = sessions
            .Select(session => new SessionDiagnostic(
                session.Title,
                session.Kind,
                session.ProcessId,
                session.ProcessId is { } processId ? ProcessTree.Sum(rows, processId).WorkingSetBytes : 0))
            .ToList();

        return SelfReadSnapshot() with
        {
            Sessions = sessionDiagnostics,
            CrashLogs = crashLogReader.RecentEntries(MaxCrashEntries),
        };
    }

    /// <summary>
    /// The sections that read only this process — platform, render backend, and native/managed memory — with no
    /// sessions or crash logs. These need nothing injected, so the panel can show them even where the collector is
    /// not registered (the design-time previewer and the screenshotter), rather than a blank "unavailable".
    /// </summary>
    public static DiagnosticsSnapshot SelfReadSnapshot() => new(
        DateTimeOffset.Now,
        PlatformInfo.Current(_ToolkitVersion(), _AppVersion()),
        RenderBackend.Describe(),
        ProcessMemoryInfo.Current(),
        ManagedHeapInfo.Current(),
        MachineMemory.TotalBytes(),
        [],
        []);

    private static string _ToolkitVersion() => _InformationalVersion(typeof(Avalonia.Application).Assembly);

    private static string _AppVersion() =>
        _InformationalVersion(Assembly.GetEntryAssembly() ?? typeof(DiagnosticsCollector).Assembly);

    // Prefer the informational version (carries the semver), dropping the "+<git sha>" the SDK appends; fall back to
    // the assembly version when it is absent, the same order AboutInfo uses for the About dialog.
    private static string _InformationalVersion(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var buildMetadata = informational.IndexOf('+');
            return buildMetadata < 0 ? informational : informational[..buildMetadata];
        }

        return assembly.GetName().Version?.ToString() ?? "unknown";
    }
}

/// <summary>What the diagnostics collector needs to know about one open session (AC-58): built from the session view
/// models by the cockpit, so the collector stays free of any view-model dependency.</summary>
public sealed record SessionDescriptor(string Title, string Kind, int? ProcessId);

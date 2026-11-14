using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Toasts;
using Cockpit.Plugins.Shared;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Sessions;

internal sealed class GuardNotifications(ISessionRegistry sessions, ILogger<GuardNotifications> logger, IToastService? toasts = null) : ISingletonService
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Pane, string Kind, string Target), DateTimeOffset> _reported = [];

    public void Report(string paneId, GuardViolation violation)
    {
        if (violation.Target is null || violation.Tool is null || violation.Kind is not ("path" or "kill") || violation.Mode is not ("Warn" or "Strict"))
        {
            return;
        }
        var session = sessions.Find(paneId) ?? (sessions.Assistant?.PaneId == paneId ? sessions.Assistant : null);
        if (session is null)
        {
            return;
        }
        var now = DateTimeOffset.UtcNow;
        var target = violation.Kind == "path" && OperatingSystem.IsWindows() ? violation.Target.ToUpperInvariant() : violation.Target;
        lock (_gate)
        {
            foreach (var expired in _reported.Where(item => now - item.Value >= TimeSpan.FromMinutes(5)).Select(item => item.Key).ToArray())
            {
                _reported.Remove(expired);
            }
            if (!_reported.TryAdd((paneId, violation.Kind, target), now))
            {
                return;
            }
        }
        var operation = violation.Kind == "kill" ? "stop outside the session boundary" :
            violation.Tool is "access" or "shell" or "start_run" ? "access outside the session boundary" : "write outside the session boundary";
        var display = violation.Target.Length > 512 ? violation.Target[..512] + "…" : violation.Target;
        var decision = violation.Denied ? "denied" : "warned";
        toasts?.Show($"{session.Title}: {operation} {decision}: {display}", ToastSeverity.Warning,
            [], false, "Cockpit", null, "Session guard");
        logger.LogInformation("Guard {Mode} {Tool} {Decision} for session {PaneId}: {Target}", violation.Mode, violation.Tool, decision, paneId, display);
    }

    public IDisposable Watch(string paneId, string statusFile)
    {
        var path = GuardJournal.PathFor(statusFile + ".children.json");
        var gate = new Lock();
        long position = 0;
        return new Timer(_ =>
        {
            lock (gate)
            {
                try
                {
                    foreach (var entry in GuardJournal.Read(path, ref position))
                    {
                        if (entry.PaneId == paneId && entry.Violation is { } violation)
                        {
                            Report(paneId, violation);
                        }
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(exception, "Could not read guard journal for {PaneId}", paneId);
                }
            }
        }, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }
}

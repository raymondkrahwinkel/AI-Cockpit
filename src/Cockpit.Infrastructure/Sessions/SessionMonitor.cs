using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Assistant;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Sessions;

// AC-1312: automatic safety net; the host remembers incidents across assistant conversation restarts.
public sealed class SessionMonitor(
    IAgentMessageInbox inbox,
    INotificationSettingsStore settingsStore,
    SessionWatcher watcher,
    ILogger<SessionMonitor> logger) : ISingletonService, IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private const string SenderPaneId = "cockpit-session-monitor";
    private const string TruncationMarker = " … (more session details omitted)";
    private const int MaxMessageLength = 2_000;
    private const string Failed = "failed";
    private const string Abandoned = "abandoned-processes";

    private readonly Dictionary<string, Observation> _lastSeen = new(StringComparer.Ordinal);
    private readonly HashSet<(string PaneId, string Signal)> _reported = [];
    private readonly Queue<DateTimeOffset> _sent = new();
    private ITimer? _timer;
    private int _looking;
    private bool _disposed;

    public Func<IReadOnlyList<ISessionHandle>>? Watching { get; set; }

    public TimeProvider Time { get; set; } = TimeProvider.System;

    public Func<ISessionHandle, Task<IReadOnlyList<string>>> Tail { get; set; } = async handle =>
        handle.HasReadableTranscript
            ? (await handle.ReadTranscriptAsync(5).ConfigureAwait(false)).Entries.Select(entry => entry.Text).ToList()
            : [];

    public void Start()
    {
        if (_timer is not null || _disposed)
        {
            return;
        }

        _timer = Time.CreateTimer(_ => _OnTick(), null, Interval, Interval);
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || Watching is not { } watching || Interlocked.CompareExchange(ref _looking, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var live = new Dictionary<string, Observation>(StringComparer.Ordinal);
            foreach (ISessionHandle handle in watching())
            {
                try
                {
                    SessionWakeState state = await handle.ReadWakeStateAsync().ConfigureAwait(false);
                    bool needsAttention = state.SessionStatus == SessionStatus.NeedsAttention || state.HasPendingConsent
                        || (await handle.ReadPendingPermissionsAsync().ConfigureAwait(false)).Count > 0;
                    live[handle.PaneId] = new Observation(handle, state.SessionStatus, needsAttention,
                        watcher.CaptureArming(handle.PaneId, SessionWatchEvents.Gone));
                }
                catch (Exception exception)
                {
                    logger.LogDebug(exception, "Reading monitor state for {PaneId} failed; the next tick tries again.", handle.PaneId);
                    if (_lastSeen.TryGetValue(handle.PaneId, out Observation? previous))
                    {
                        live[handle.PaneId] = previous;
                    }
                }
            }

            var active = new Dictionary<(string PaneId, string Signal), Observation>();
            foreach (Observation session in live.Values)
            {
                string paneId = session.Handle.PaneId;
                if (session.Status == SessionStatus.Failed)
                {
                    active[(paneId, Failed)] = session;
                }
                if (session.NeedsAttention)
                {
                    active[(paneId, SessionWatchEvents.NeedsAttention)] = session;
                }
                if (session.Status != SessionStatus.Busy && session.Handle.AbandonedProcessCount > 0)
                {
                    active[(paneId, Abandoned)] = session;
                }
            }

            foreach ((string paneId, Observation last) in _lastSeen)
            {
                if (!live.ContainsKey(paneId) && last.Status is not (SessionStatus.Idle or SessionStatus.Done)
                    && !last.GoneArmed())
                {
                    active[(paneId, SessionWatchEvents.Gone)] = last;
                }
            }

            _reported.IntersectWith(active.Keys);
            var findings = active.Where(entry => !_reported.Contains(entry.Key) && !_IsArmed(entry.Key)).ToList();
            if (findings.Count > 0)
            {
                var settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
                DateTimeOffset now = Time.GetUtcNow();
                while (_sent.TryPeek(out DateTimeOffset sent) && now - sent >= TimeSpan.FromHours(1))
                {
                    _sent.Dequeue();
                }

                if (_sent.Count >= Math.Max(1, settings.MonitorMessagesPerHour))
                {
                    _Remember(live, active);
                    return;
                }

                var summaries = new List<string>();
                var transcripts = new List<string>();
                foreach (var group in findings.GroupBy(entry => entry.Key.PaneId))
                {
                    Observation session = group.First().Value;
                    string signals = string.Join(", ", group.Select(entry => entry.Key.Signal));
                    IReadOnlyList<string> rows = await _TailAsync(session.Handle).ConfigureAwait(false);
                    string tail = string.Join("\n", rows.TakeLast(5).Select(row => "> " + _Short(row, 200)));
                    string abandoned = group.Any(entry => entry.Key.Signal == Abandoned)
                        ? $"; {session.Handle.AbandonedProcessCount} abandoned processes" : string.Empty;
                    summaries.Add($"Session '{_Short(session.Handle.Title, 120)}' ({group.Key}) — {signals}{abandoned}; last status: {session.Status}.");
                    if (tail.Length > 0)
                    {
                        transcripts.Add($"{group.Key}:\n{tail}");
                    }
                }

                // Put identities before transcripts so a long tail cannot hide the other affected panes.
                string body = string.Join("\n", summaries) + "\n\n" + string.Join("\n\n", transcripts)
                    + "\nNothing has been started about it.";
                if (body.Length > MaxMessageLength)
                {
                    body = body[..(MaxMessageLength - TruncationMarker.Length)] + TruncationMarker;
                }

                AgentMessageDelivery delivery = inbox.Deliver(SenderPaneId, AssistantIdentity.PaneId, "session", body,
                    subjectPaneId: summaries.Count == 1 ? findings[0].Key.PaneId : null);
                if (delivery.Outcome == AgentMessageDeliveryOutcome.RecipientInboxFull)
                {
                    _Remember(live, active);
                    return;
                }

                _sent.Enqueue(now);
                _reported.UnionWith(findings.Select(entry => entry.Key));
                logger.LogInformation("Session monitor delivered {Count} findings to the assistant.", findings.Count);
            }

            _Remember(live, active);
        }
        finally
        {
            Interlocked.Exchange(ref _looking, 0);
        }
    }

    private bool _IsArmed((string PaneId, string Signal) finding) => watcher.IsArmed(finding.PaneId,
        finding.Signal == Failed ? SessionWatchEvents.BusyToIdle : finding.Signal);

    private void _Remember(Dictionary<string, Observation> live, Dictionary<(string PaneId, string Signal), Observation> active)
    {
        // A disappeared pane held by the ceiling or a full inbox must survive until its report is accepted.
        foreach (var entry in active.Where(entry => entry.Key.Signal == SessionWatchEvents.Gone && !_reported.Contains(entry.Key)))
        {
            live.TryAdd(entry.Key.PaneId, entry.Value);
        }
        _lastSeen.Clear();
        foreach ((string paneId, Observation session) in live)
        {
            _lastSeen[paneId] = session;
        }
    }

    private async Task<IReadOnlyList<string>> _TailAsync(ISessionHandle handle)
    {
        try
        {
            return await Tail(handle).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Reading monitor transcript for {PaneId} failed; reporting without it.", handle.PaneId);
            return [];
        }
    }

    private static string _Short(string text, int limit)
    {
        string single = text.ReplaceLineEndings(" ").Trim();
        return single.Length <= limit ? single : single[..limit] + "…";
    }

    private async void _OnTick()
    {
        try
        {
            await RunOnceAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "A session monitor tick failed; the next tick tries again.");
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer?.Dispose();
        _timer = null;
    }

    private sealed record Observation(ISessionHandle Handle, SessionStatus Status, bool NeedsAttention, Func<bool> GoneArmed);
}

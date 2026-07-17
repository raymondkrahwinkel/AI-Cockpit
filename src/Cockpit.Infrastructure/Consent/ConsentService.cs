using System.Collections.Concurrent;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Consent;
using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Infrastructure.Consent;

/// <summary>
/// The host's consent gate (#AC-47). Holds each waiting request as a <see cref="TaskCompletionSource{TResult}"/>
/// the UI resolves, the session's set of remembered low-risk approvals, and writes every decision to the audit
/// trail. Single instance so all callers share one remember-set and one list of open prompts.
/// </summary>
internal sealed class ConsentService(IConsentAuditLog auditLog) : IConsentBroker, ISingletonService
{
    private readonly ConcurrentDictionary<Guid, _Pending> _pending = new();

    // Actions the operator chose to stop being asked about this session. Keyed on the whole approved request —
    // pane, the host-stamped plugin id, scope, AND the literal action — never on the caller-controlled pane+scope
    // alone: keying on a subset let a remembered "GET the issues" silently approve a later "GET evil.com/exfil" the
    // operator never saw, and let one plugin ride another's remembered approval. Only ever low-risk entries — the
    // dangerous class is never added, so it is always asked afresh.
    private readonly ConcurrentDictionary<(string? PaneId, string? PluginId, string Scope, string Action), byte> _remembered = new();

    public event EventHandler<ConsentPrompt>? PromptOpened;

    public event EventHandler<Guid>? PromptClosed;

    public async Task<ConsentDecision> RequestConsentAsync(ConsentRequest request, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return await _FailClosedAsync(request).ConfigureAwait(false);
        }

        // A remembered scope skips the prompt — but only for the low-risk class, so a single earlier approval can
        // never let a later dangerous call ride along on it.
        if (request.Risk == ConsentRisk.LowRisk && _remembered.ContainsKey(_Key(request)))
        {
            await _RecordAsync(request, ConsentOutcome.Approved, remembered: true).ConfigureAwait(false);
            return new ConsentDecision(ConsentOutcome.Approved, Remembered: true);
        }

        var handler = PromptOpened;
        if (handler is null)
        {
            // Nothing is listening to show a prompt — deny rather than block forever or approve blindly.
            return await _FailClosedAsync(request).ConfigureAwait(false);
        }

        // "Remember" is offered only for a low-risk action that asked for it; the broker decides this, not the
        // caller, so a request cannot make its own dangerous action rememberable by setting the flag.
        var canRemember = request.AllowRemember && request.Risk == ConsentRisk.LowRisk;

        var id = Guid.NewGuid();
        var pending = new _Pending(request, canRemember);
        _pending[id] = pending;
        pending.CtRegistration = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(() => _Cancel(id))
            : default;

        handler.Invoke(this, new ConsentPrompt(id, request, canRemember));

        return await pending.Completion.Task.ConfigureAwait(false);
    }

    public void Respond(Guid promptId, ConsentOutcome outcome, bool remember)
    {
        if (!_pending.TryRemove(promptId, out var pending))
        {
            return;
        }

        var remembered = outcome == ConsentOutcome.Approved && remember && pending.CanRemember;
        if (remembered)
        {
            _remembered[_Key(pending.Request)] = 0;
        }

        _Finish(pending, promptId, outcome, remembered);
    }

    private void _Cancel(Guid promptId)
    {
        if (_pending.TryRemove(promptId, out var pending))
        {
            _Finish(pending, promptId, ConsentOutcome.Denied, remembered: false);
        }
    }

    private void _Finish(_Pending pending, Guid promptId, ConsentOutcome outcome, bool remembered)
    {
        pending.CtRegistration.Dispose();
        _ = _RecordAsync(pending.Request, outcome, remembered);
        PromptClosed?.Invoke(this, promptId);
        pending.Completion.TrySetResult(new ConsentDecision(outcome, remembered));
    }

    private async Task<ConsentDecision> _FailClosedAsync(ConsentRequest request)
    {
        await _RecordAsync(request, ConsentOutcome.Denied, remembered: false).ConfigureAwait(false);
        return ConsentDecision.Denied;
    }

    private Task _RecordAsync(ConsentRequest request, ConsentOutcome outcome, bool remembered)
    {
        var entry = new ConsentAuditEntry(
            DateTimeOffset.UtcNow,
            outcome == ConsentOutcome.Approved ? ConsentAuditAction.Approved : ConsentAuditAction.Denied,
            request.Source.Label,
            request.Source.PaneId,
            request.Source.PluginId,
            request.Scope,
            request.Action,
            remembered);

        return auditLog.RecordAsync(entry);
    }

    // The remember key is the whole approved request, not a caller-controlled subset (see _remembered). PluginId is
    // the host-stamped identity (CockpitHost), and Action is the ground truth the operator actually saw, so a
    // different action or a different plugin never matches — it re-prompts.
    private static (string? PaneId, string? PluginId, string Scope, string Action) _Key(ConsentRequest request) =>
        (request.Source.PaneId, request.Source.PluginId, request.Scope, request.Action);

    private sealed class _Pending(ConsentRequest request, bool canRemember)
    {
        public ConsentRequest Request { get; } = request;

        public bool CanRemember { get; } = canRemember;

        public TaskCompletionSource<ConsentDecision> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationTokenRegistration CtRegistration { get; set; }
    }
}

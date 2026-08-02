using System.Collections.Concurrent;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Consent;
using Cockpit.Core.Consent;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Infrastructure.Consent;

/// <summary>
/// The host's consent gate (#AC-47). Holds each waiting request as a <see cref="TaskCompletionSource{TResult}"/>
/// the UI resolves, the session's set of remembered low-risk approvals, and writes every decision to the audit
/// trail. Single instance so all callers share one remember-set and one list of open prompts.
/// </summary>
/// <remarks>
/// <paramref name="bypassPolicy"/> is the assistant's consent bypass (#AC-575) and is optional: with none
/// registered — the design-time graph, every test that does not ask for one — nothing is ever bypassed and this
/// class behaves exactly as it did before. Fail-closed by construction rather than by a flag someone has to
/// remember to leave off.
/// </remarks>
internal sealed class ConsentService(IConsentAuditLog auditLog, IConsentBypassPolicy? bypassPolicy = null)
    : IConsentBroker, ISingletonService
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
        // AC-89: a request that arrived over a per-session MCP token carries a transport-verified pane id. Make it the
        // authoritative identity — the agent's declared session (which it could forge to ride another pane's remembered
        // approvals) is overridden here, so the remember key and the prompt routing use the session the request truly
        // came from. Off that path (the in-process tool loop, the app's own UI-side consent) the verified id is null
        // and the request is used exactly as given.
        var verifiedPaneId = McpRequestContext.CurrentPaneId;
        if (verifiedPaneId is not null)
        {
            request = request with { Source = request.Source with { PaneId = verifiedPaneId } };
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return await _FailClosedAsync(request).ConfigureAwait(false);
        }

        // AC-575: the operator can switch the card off ahead of time, per source, for the assistant only. Placed
        // here on purpose — after the override above, so the policy is handed the transport-verified pane id and
        // never the one the request carries (an agent that writes the assistant's pane id into its own
        // Source.PaneId gets a null verified id here and cannot talk its way in), and before the _remembered check
        // below, because a bypass is the stronger statement of the two and must not quietly become a remembered
        // approval the operator never gave.
        //
        // The contract this puts on callers: McpRequestContext identifies the flow, not the party whose action is
        // being gated, and it is inherited by everything that flow awaits. Anything that does work on behalf of a
        // *different* owner has to restamp it first, or the wrong identity decides both the bypass and the remember
        // key below — see DelegationService._StartAsync, which is where the queue drainer did exactly that.
        //
        // "Not low risk" rather than "is dangerous": the polarity has to fail closed. A third risk value added later
        // arrives here as dangerous:false under the equality test, which would make it bypassable on the everyday
        // switch — the one an operator ticks freely — instead of the deliberate second one.
        if (verifiedPaneId is not null
            && bypassPolicy?.ShouldBypass(verifiedPaneId, _SourceKey(request), request.Risk != ConsentRisk.LowRisk) == true)
        {
            await _RecordAsync(request, ConsentOutcome.Approved, remembered: false, bypassed: true).ConfigureAwait(false);
            return new ConsentDecision(ConsentOutcome.Approved, Remembered: false);
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

        // Show the prompt before wiring cancellation, so PromptOpened always precedes any PromptClosed: a token that
        // is already — or becomes — cancelled then fires _Cancel after the banner is up, never before it (which would
        // leave a banner whose id the broker has already forgotten, impossible to answer).
        handler.Invoke(this, new ConsentPrompt(id, request, canRemember));
        pending.CtRegistration = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(() => _Cancel(id))
            : default;

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
        PromptClosed?.Invoke(this, promptId);   // take the banner down at once
        _ = _ResolveAsync(pending, outcome, remembered);
    }

    // The caller's decision resolves only after the audit line is flushed — the same order the remembered and
    // fail-closed paths already take — so a crash can't leave the operator having acted on a decision the
    // append-only trail never recorded.
    private async Task _ResolveAsync(_Pending pending, ConsentOutcome outcome, bool remembered)
    {
        await _RecordAsync(pending.Request, outcome, remembered).ConfigureAwait(false);
        pending.Completion.TrySetResult(new ConsentDecision(outcome, remembered));
    }

    private async Task<ConsentDecision> _FailClosedAsync(ConsentRequest request)
    {
        await _RecordAsync(request, ConsentOutcome.Denied, remembered: false).ConfigureAwait(false);
        return ConsentDecision.Denied;
    }

    private Task _RecordAsync(ConsentRequest request, ConsentOutcome outcome, bool remembered, bool bypassed = false)
    {
        var entry = new ConsentAuditEntry(
            DateTimeOffset.UtcNow,
            bypassed ? ConsentAuditAction.Bypassed
                : outcome == ConsentOutcome.Approved ? ConsentAuditAction.Approved : ConsentAuditAction.Denied,
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

    /// <summary>
    /// Who asked, for the bypass switches (#AC-575) — the host-stamped plugin id (<c>CockpitHost</c> sets it and a
    /// plugin cannot ask under another's name) under its own prefix, or the label, which for a host-internal caller
    /// is a compile-time constant in <see cref="ConsentSourceCatalog"/>. The prefixing rule lives there, next to
    /// those constants, because the Options list has to build the identical key.
    /// </summary>
    /// <remarks>
    /// Scope and Action are absent for the same reason <see cref="_remembered"/> includes them: they are text an
    /// agent influences. There the whole request is the key so a remembered "GET the issues" cannot approve a later
    /// "GET evil.com/exfil"; here the operator is switching off a <em>source</em>, so agent-authored text must not
    /// be able to name a source that is not its own.
    /// </remarks>
    private static string _SourceKey(ConsentRequest request) =>
        ConsentSourceCatalog.KeyFor(request.Source.PluginId, request.Source.Label);

    private sealed class _Pending(ConsentRequest request, bool canRemember)
    {
        public ConsentRequest Request { get; } = request;

        public bool CanRemember { get; } = canRemember;

        public TaskCompletionSource<ConsentDecision> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationTokenRegistration CtRegistration { get; set; }
    }
}

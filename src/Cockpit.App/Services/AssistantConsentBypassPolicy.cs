using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Consent;
using Cockpit.Core.Assistant;

namespace Cockpit.App.Services;

// The one implementation of `IConsentBypassPolicy` (#AC-575): the only place that knows both which
// pane id is the assistant's and which sources the operator switched the consent card off for.
// *Four conditions, all four required.* A request is bypassed only when the transport-verified pane is the
// assistant's, the assistant is switched on at all, the source is in the operator's list, and — for a dangerous
// action — in the second list as well. Any one of them failing shows the card exactly as before. They are checked
// in that order because the first is the cheapest and the one an attacker would have to beat first.
//
// *The settings are a snapshot, not a read per request.* `IConsentBypassPolicy.ShouldBypass` is
// synchronous — the broker calls it in the middle of deciding — and the store reads a file. The snapshot is loaded
// at construction and replaced whenever Options saves (the shell wires `ApplySettingsAsync` to the
// same `Saved` event the hotkey and the chip already follow), so switching a source off takes effect on the
// next request rather than at the next restart. It starts empty, so the window before the first load has finished
// bypasses nothing.
//
// *Case.* Sources are compared ordinally and case-sensitively. They are not typed by a human — the list in
// Options is filled from host-stamped names — so a case-insensitive match would only ever widen the set of things
// that count as the same source, and widening is the direction that costs something here.
public sealed class AssistantConsentBypassPolicy : IConsentBypassPolicy, ISingletonService
{
    private readonly IAssistantSettingsStore _settings;

    // The switches as last read. One immutable object replaced wholesale rather than two fields written in turn:
    // the broker reads this off whatever thread the MCP request arrived on, and a half-applied update is a moment
    // in which the dangerous list belongs to a different save than the low-risk one.
    private volatile _Switches _current = _Switches.Empty;

    public AssistantConsentBypassPolicy(IAssistantSettingsStore settings)
    {
        _settings = settings;

        // Loaded here because there is no startup hook on the path that builds the consent broker, and the first
        // consent request can arrive before any Options page has been opened. Fire-and-forget: a failed read leaves
        // the empty snapshot, which bypasses nothing.
        _ = ApplySettingsAsync();
    }

    // Re-reads the switches. Called at construction and whenever Options saves.
    public async Task ApplySettingsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _current = _Switches.From(await _settings.LoadAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception)
        {
            // An unreadable config must not leave a stale, wider snapshot in place: fall back to bypassing nothing.
            _current = _Switches.Empty;
        }
    }

    public bool ShouldBypass(string? verifiedPaneId, string sourceKey, bool dangerous)
    {
        // 1. The verified pane is the assistant's. Never an ordinary pane, and never a request that arrived on no
        //    verified session at all — the broker only ever hands the transport-stamped id here, so a request whose
        //    Source.PaneId was filled in with this constant by the agent itself arrives as a null and stops on this
        //    line. That is the whole of why the check sits where it does in the broker.
        if (verifiedPaneId is null || !string.Equals(verifiedPaneId, AssistantIdentity.PaneId, StringComparison.Ordinal))
        {
            return false;
        }

        var current = _current;

        // 2. The assistant is switched on. A bypass belonging to a feature that is off is a permission nobody is
        //    watching; turning the assistant off has to take its exemptions with it.
        if (!current.AssistantEnabled)
        {
            return false;
        }

        // 3/4. The source is on the operator's list — and a dangerous action needs the second list, which is not
        //      implied by the first. A source in neither list is asked about exactly as it is today.
        return dangerous
            ? current.Dangerous.Contains(sourceKey)
            : current.LowRisk.Contains(sourceKey);
    }

    private sealed record _Switches(bool AssistantEnabled, IReadOnlySet<string> LowRisk, IReadOnlySet<string> Dangerous)
    {
        public static readonly _Switches Empty =
            new(AssistantEnabled: false, new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));

        public static _Switches From(AssistantSettings settings) => new(
            settings.IsEnabled,
            new HashSet<string>(settings.ConsentBypassSources, StringComparer.Ordinal),
            new HashSet<string>(settings.ConsentBypassDangerousSources, StringComparer.Ordinal));
    }
}

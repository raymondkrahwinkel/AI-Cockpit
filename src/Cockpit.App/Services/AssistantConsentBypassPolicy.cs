using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Consent;
using Cockpit.Core.Assistant;

namespace Cockpit.App.Services;

// AC-1013: the one `IConsentBypassPolicy` (#AC-575) — checks verified pane id, then assistant-enabled, then
// "allow all" (#AC-637) or the per-source/dangerous lists, in that cheapest-first order; settings are a
// synchronous in-memory snapshot (reloaded on Options save, empty until first load) compared case-sensitively.
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
        // AC-1013: 1. the verified pane must be the assistant's — never an ordinary pane, and never a request
        // with no verified session (a self-stamped Source.PaneId arrives as null and stops here).
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

        // 3. "Allow all" (#AC-637): one switch for every source and both risk classes, so nothing below is reached.
        if (current.All)
        {
            return true;
        }

        // 4/5. The source is on the operator's list — and a dangerous action needs the second list, which is not
        //      implied by the first. A source in neither list is asked about exactly as it is today.
        return dangerous
            ? current.Dangerous.Contains(sourceKey)
            : current.LowRisk.Contains(sourceKey);
    }

    private sealed record _Switches(bool AssistantEnabled, bool All, IReadOnlySet<string> LowRisk, IReadOnlySet<string> Dangerous)
    {
        // `All: false` even though the setting itself defaults to on: this is the snapshot for "no settings read
        // yet" and "the config would not read", where the honest answer is to ask rather than to assume the wider one.
        public static readonly _Switches Empty =
            new(AssistantEnabled: false, All: false, new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));

        public static _Switches From(AssistantSettings settings) => new(
            settings.IsEnabled,
            settings.ConsentBypassAll,
            new HashSet<string>(settings.ConsentBypassSources, StringComparer.Ordinal),
            new HashSet<string>(settings.ConsentBypassDangerousSources, StringComparer.Ordinal));
    }
}

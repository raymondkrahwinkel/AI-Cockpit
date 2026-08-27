using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.ViewModels;

// Both kinds carry all four remaining fields: for TTY these are launch-only start defaults passed as CLI flags
// (`--permission-mode`/ `--dangerously-skip-permissions`, `--model`, `--effort`) — once running, the real TUI owns any
// live switching itself (`/model`, `/effort`, Shift+Tab), since TTY (#31, #32, #44, AC-85, AC-138, AC-163, AC-142,
public sealed record NewSessionResult(
    SessionKind Kind,
    SessionProfile Profile,
    PermissionModeOption Mode,
    ModelOption Model,
    EffortOption Effort,
    string? SessionName,
    IReadOnlySet<string>? EnabledMcpServerNames = null,
    string? WorkingDirectory = null,
    SessionResume? Resume = null,
    IReadOnlyDictionary<string, string>? PluginTtyOptions = null,
    IReadOnlyDictionary<string, string>? SdkLaunchOptions = null,
    bool IsolateInWorktree = false,
    ReadingLevel? ReadingLevel = null,
    string? ProjectId = null,
    string? SystemPrompt = null)
{
    // Whether `SessionName` was put together by the cockpit rather than chosen by anybody — "Cockpit 2", "Claude —
    // 14:22", "webshop (copy)" (AC-310, AC-324).
    public bool NameIsComposed { get; init; }

    // Whether the session this starts carries a name somebody meant, and so one a ticket linked to it later must
    // leave alone (#AC-310). The whole rule, in one expression: a name is chosen when there is one and nobody
    // composed it. Everything downstream applies this rather than working it out again (#AC-324).
    public bool NameIsChosen => !NameIsComposed && !string.IsNullOrWhiteSpace(SessionName);

    // The SDK provider's launch options with `SystemPrompt` (or its fallback, AC-544) folded in.
    public IReadOnlyDictionary<string, string>? SdkLaunchOptionsWithInstructions => _WithSystemPrompt(SdkLaunchOptions);

    // The TTY provider's launch options with `SystemPrompt` (or its fallback, AC-544) folded in.
    public IReadOnlyDictionary<string, string>? TtyLaunchOptionsWithInstructions => _WithSystemPrompt(PluginTtyOptions);

    // AC-544 criterion 5: `AgentStatusSystemPrompt.Default` rides *alongside* a profile's own prompt rather than being
    // replaced by it, because that is what the criterion's own precedent does.
    private IReadOnlyDictionary<string, string>? _WithSystemPrompt(IReadOnlyDictionary<string, string>? options)
    {
        var merged = options is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(options, StringComparer.Ordinal);

        // The profile's own words first, the standing instruction after — the same order, and the same blank-line
        // join, that _AppendedInstructions already uses for the delegation nudge.
        merged[WellKnownPluginSessionOptions.AppendSystemPrompt] = string.IsNullOrWhiteSpace(SystemPrompt)
            ? AgentStatusSystemPrompt.Default
            : SystemPrompt.Trim() + "\n\n" + AgentStatusSystemPrompt.Default;
        return merged;
    }
}

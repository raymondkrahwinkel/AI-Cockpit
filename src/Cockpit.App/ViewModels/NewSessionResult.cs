using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;

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

    // AC-1300: whether the cockpit's own assistant asked for this session. False for every operator-driven start —
    // the New-session dialog, the launcher, Duplicate — which is what keeps a session the operator opened out of the
    // assistant's relation even when it lives beside one the assistant started.
    public bool StartedByTheAssistant { get; init; }

    // AC-490: the project job this session was started from, or null for every other start. What makes the session a
    // run of that job — recorded against it once it exists — and what gets the agent asked to report on the work.
    public string? ProjectJobId { get; init; }

    // Whether the session this starts carries a name somebody meant, and so one a ticket linked to it later must
    // leave alone (#AC-310). The whole rule, in one expression: a name is chosen when there is one and nobody
    // composed it. Everything downstream applies this rather than working it out again (#AC-324).
    public bool NameIsChosen => !NameIsComposed && !string.IsNullOrWhiteSpace(SessionName);
}

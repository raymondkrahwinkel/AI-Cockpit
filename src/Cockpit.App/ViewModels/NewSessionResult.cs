using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.ViewModels;

/// <summary>
/// The choices confirmed in the New-session dialog, handed to the cockpit to mint and immediately
/// start a session (#31/#32). <see cref="Kind"/> is picked inside the dialog itself and tells the
/// cockpit which session type to create. Both kinds carry all four remaining fields: for TTY these are
/// launch-only start defaults passed as CLI flags (<c>--permission-mode</c>/
/// <c>--dangerously-skip-permissions</c>, <c>--model</c>, <c>--effort</c>) — once running, the real TUI
/// owns any live switching itself (<c>/model</c>, <c>/effort</c>, Shift+Tab), since TTY mode has no
/// control channel.
/// </summary>
/// <param name="EnabledMcpServerNames">
/// The per-session MCP-server selection (#44) picked in the dialog's checklist of the shared registry's
/// enabled servers — <see langword="null"/> when the dialog found no registry servers to offer, meaning
/// no session-level restriction applies on top of the registry's own enabled/scope filtering. Consumed by
/// the Claude SDK/local-model tool-loop (<c>McpToolProvider</c>) and the Claude-CLI <c>--mcp-config</c>
/// fan-out (<c>ClaudeCliProcess</c>); the TTY driver does not fan the registry out at all today, so this
/// has no effect there.
/// </param>
/// <param name="WorkingDirectory">
/// An optional per-session working directory chosen in the dialog (e.g. a project folder), overriding the
/// global <c>Claude:WorkingDirectory</c> option for this one session — the directory <c>claude</c> is
/// launched in, for both the SDK process and the TTY pty. <see langword="null"/>/blank keeps the global
/// default (the configured option, else the app's current directory).
/// </param>
/// <param name="PluginTtyOptions">
/// The start defaults chosen for a <em>plugin</em> TTY provider's own declared options (Codex's sandbox
/// policy, say) — keyed exactly as that provider's <c>TtyProviderRegistration.Options</c> declared them.
/// <see langword="null"/> for a Claude session (which carries its start defaults through
/// <see cref="Mode"/>/<see cref="Model"/>/<see cref="Effort"/> instead) or a profile with no declared
/// options. The two never both apply to the same launch: <see cref="Mode"/>/<see cref="Model"/>/<see cref="Effort"/>
/// are Claude's vocabulary and this is everyone else's.
/// </param>
/// <param name="IsolateInWorktree">
/// Whether to run this session in its own git worktree on a dedicated branch (AC-85) when
/// <see cref="WorkingDirectory"/> is a git repository — a per-session choice made in the dialog next to the
/// folder, not a profile setting. Ignored for a non-repository folder.
/// </param>
/// <param name="ReadingLevel">
/// The reading level (AC-138) this SDK session opens with, overriding the profile's default view for this one
/// session — chosen in the dialog and shown only for an SDK session. <see langword="null"/> keeps the profile
/// default (the New-session dialog seeds it from there). Ignored for a TTY session, which has no reading level.
/// </param>
/// <param name="ProjectId">
/// The project this session works on (AC-163), or <see langword="null"/> for one belonging to none. Carried so the
/// running session can resolve its project's MCP overlay — everything downstream picks servers by name out of the
/// catalog, and which names exist depends on the project.
/// </param>
/// <param name="SystemPrompt">
/// The standing instructions to append to the provider's own system prompt: the profile's identity (AC-142) with
/// the project's behaviour under it, already resolved by <c>SessionStartDefaults</c>. <see langword="null"/> (or
/// blank) does not mean "appends nothing" any more (AC-544): <see cref="SdkLaunchOptionsWithInstructions"/> and
/// <see cref="TtyLaunchOptionsWithInstructions"/> fall back to <see cref="Cockpit.Core.Sessions.AgentStatusSystemPrompt.Default"/>
/// in that case, so a session with no profile identity still starts knowing to keep its own statusline current.
/// </param>
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
    /// <summary>
    /// Whether <see cref="SessionName"/> was put together by the cockpit rather than chosen by anybody — "Cockpit 2",
    /// "Claude — 14:22", "webshop (copy)". A composed name is a placeholder, so a ticket linked to that session later
    /// may still label it; a name somebody typed, renamed to, or handed in is theirs and stays (#AC-310).
    /// <para>
    /// It rides along here so the one place that decides — <c>AddSession</c> — can be told, instead of every route
    /// that composes a name having to put the flag back afterwards. Three of them did; the fourth forgot, and a
    /// session started by a flow could never be relabelled until that was found (#AC-324).
    /// </para>
    /// </summary>
    public bool NameIsComposed { get; init; }

    /// <summary>
    /// Whether the session this starts carries a name somebody meant, and so one a ticket linked to it later must
    /// leave alone (#AC-310). The whole rule, in one expression: a name is chosen when there is one and nobody
    /// composed it. Everything downstream applies this rather than working it out again (#AC-324).
    /// </summary>
    public bool NameIsChosen => !NameIsComposed && !string.IsNullOrWhiteSpace(SessionName);

    /// <summary>The SDK provider's launch options with <see cref="SystemPrompt"/> (or its fallback, AC-544) folded in.</summary>
    public IReadOnlyDictionary<string, string>? SdkLaunchOptionsWithInstructions => _WithSystemPrompt(SdkLaunchOptions);

    /// <summary>The TTY provider's launch options with <see cref="SystemPrompt"/> (or its fallback, AC-544) folded in.</summary>
    public IReadOnlyDictionary<string, string>? TtyLaunchOptionsWithInstructions => _WithSystemPrompt(PluginTtyOptions);

    /// <summary>
    /// <paramref name="options"/> carrying the resolved instructions under the well-known append-system-prompt key,
    /// which every provider already honours (Claude TTY and SDK, the OpenAI-compatible drivers, Codex) — the same
    /// channel the delegation and Autopilot briefs use, so a profile's identity needs no per-provider plumbing of
    /// its own.
    /// <para>
    /// AC-544 criterion 5: <see cref="AgentStatusSystemPrompt.Default"/> rides <em>alongside</em> a profile's own
    /// prompt rather than being replaced by it, because that is what the criterion's own precedent does. The
    /// delegation instruction travels as its own value and
    /// <c>ClaudeTtyProvider._AppendedInstructions</c> joins it to whatever the profile resolved — a profile with an
    /// identity prompt still gets the orchestrator nudge. Dropping the status instruction the moment a profile has
    /// anything to say would invert that, and invert it in the worst direction: the profiles that carry a written
    /// identity are the considered ones doing ticket work, so exactly the sessions whose status matters most would
    /// be the ones that silently stopped being told to keep it.
    /// </para>
    /// <para>
    /// <b>Still replaceable per profile</b>, in the way a system prompt is replaceable at all: the profile's own
    /// text comes first and a later instruction that contradicts it is the operator's to write. What a profile
    /// cannot do is lose the instruction by accident, which is the failure mode worth engineering against — an
    /// operator who does not want it can say so, and one who never thought about it keeps it.
    /// </para>
    /// </summary>
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

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
/// the project's behaviour under it, already resolved by <c>SessionStartDefaults</c>. <see langword="null"/>
/// appends nothing.
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
    /// <summary>The SDK provider's launch options with <see cref="SystemPrompt"/> folded in (AC-142).</summary>
    public IReadOnlyDictionary<string, string>? SdkLaunchOptionsWithInstructions => _WithSystemPrompt(SdkLaunchOptions);

    /// <summary>The TTY provider's launch options with <see cref="SystemPrompt"/> folded in (AC-142).</summary>
    public IReadOnlyDictionary<string, string>? TtyLaunchOptionsWithInstructions => _WithSystemPrompt(PluginTtyOptions);

    /// <summary>
    /// <paramref name="options"/> carrying the resolved instructions under the well-known append-system-prompt key,
    /// which every provider already honours (Claude TTY and SDK, the OpenAI-compatible drivers, Codex) — the same
    /// channel the delegation and Autopilot briefs use, so a profile's identity needs no per-provider plumbing of
    /// its own. Returns the options untouched when there is nothing to say.
    /// </summary>
    private IReadOnlyDictionary<string, string>? _WithSystemPrompt(IReadOnlyDictionary<string, string>? options)
    {
        if (string.IsNullOrWhiteSpace(SystemPrompt))
        {
            return options;
        }

        var merged = options is null
            ? []
            : new Dictionary<string, string>(options, StringComparer.Ordinal);

        merged[WellKnownPluginSessionOptions.AppendSystemPrompt] = SystemPrompt.Trim();
        return merged;
    }
}

using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cockpit.App.ViewModels;

// One checkbox row in an MCP-server checklist: a server's name plus whether it is ticked. Used both in the
// New-session dialog for the per-session selection (#44) and in the profile editor for a profile's saved
// pre-selection (AC-130). Defaults to checked, matching the pre-#44 behaviour of loading every enabled server.
// Carries an optional pre-flight tool-token estimate (AC-134) so each row can show roughly what the server adds.
public partial class McpServerSelectionItemViewModel : ViewModelBase
{
    public string Name { get; }

    [ObservableProperty]
    private bool _isEnabledForSession = true;

    // True while the estimate is being enumerated in the background, so the row can show a placeholder.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokenLabel))]
    [NotifyPropertyChangedFor(nameof(TokenTooltip))]
    private bool _isEstimatingTokens;

    // The pre-flight tool-token estimate for this server (AC-134), or null before one has been computed.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokenLabel))]
    [NotifyPropertyChangedFor(nameof(TokenTooltip))]
    private McpServerToolEstimate? _tokenEstimate;

    // What the cockpit knows about this server's OAuth standing (AC-355), read once when the checklist is built —
    // null for a server the dialog never asked (no coordinator, e.g. the design-time constructor) or that turned
    // out not to need one (`McpAuthState.NotRequired` is folded to null here too, since neither
    // case has anything worth telling the operator).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokenTooltip))]
    private McpAuthState? _authState;

    // The per-row token figure next to the checkbox: blank before an estimate exists, "…" while counting, "?" when
    // the server could not be enumerated (unreachable or needs auth), else "~4.2k" (AC-134).
    public string TokenLabel =>
        IsEstimatingTokens ? "…"
        : TokenEstimate is not { } estimate ? string.Empty
        : !estimate.Available ? "?"
        : $"~{McpToolTokenMath.Format(estimate.EstimatedTokens)}";

    // Hover text explaining the per-row figure (AC-134) — most usefully the "?": a server counts as unknown when
    // it could not be reached to list its tools. That reason is usually a shrug ("offline, needs a sign-in, or its
    // plugin isn't loaded"), but for an OAuth server the cockpit already knows which one applies
    // (`AuthState` from AC-355's status read), so the tooltip says that instead of guessing — the pre-
    // flight count itself still opens no browser (`McpAuthState.AuthorizationRequired` tells the
    // operator to sign in, it does not sign them in). Null when there is nothing worth explaining (no estimate yet).
    public string? TokenTooltip =>
        IsEstimatingTokens ? "Counting this server's tools…"
        : TokenEstimate is not { } estimate ? null
        : !estimate.Available
            ? AuthState == McpAuthState.AuthorizationRequired
                ? "Couldn't count this server's tools — it needs a sign-in first. Sign in from the MCP servers dialog, then re-check."
                : "Couldn't reach this server to count its tools — it may be offline, need a sign-in, or its plugin isn't loaded."
        : $"{estimate.ToolCount} tool{(estimate.ToolCount == 1 ? string.Empty : "s")}, ~{McpToolTokenMath.Format(estimate.EstimatedTokens)} tokens (estimate)";

    public McpServerSelectionItemViewModel(string name)
    {
        Name = name;
    }
}

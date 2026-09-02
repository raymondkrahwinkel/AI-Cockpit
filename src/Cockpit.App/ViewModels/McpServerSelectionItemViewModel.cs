using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cockpit.App.ViewModels;

// One checkbox row in an MCP-server checklist: a server's name plus whether it is ticked (#44, AC-130, AC-134).
public partial class McpServerSelectionItemViewModel : ViewModelBase
{
    public string Name { get; }

    // Whether the catalog offered this row only because the project being edited names its scheme
    // (McpServerConfig.ProjectLinked, AC-766) — never true outside the project editor, since only a scoped catalog
    // query can mark one.
    public bool IsProjectLinked { get; }

    [ObservableProperty]
    private bool _isEnabledForSession = true;

    // Whether this row's box may be ticked at all. Off only in the project editor, for an ordinary row while that
    // project pre-selects nothing (AC-130's off-state): every server goes along, so a tick that would be dropped
    // on save cannot be made in the first place.
    [ObservableProperty]
    private bool _isTickEnabled = true;

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

    // What the cockpit knows about this server's OAuth standing (AC-355), read once when the checklist is built — null
    // for a server the dialog never asked (no coordinator, e.g.
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

    // That reason is usually a shrug ("offline, needs a sign-in, or its plugin isn't loaded"), but for an OAuth server
    // the cockpit already knows which one applies (`AuthState` from AC-355's status read), so the tooltip says that
    // instead of guessing — the pre- flight count itself still opens no browser (AC-134).
    public string? TokenTooltip =>
        IsEstimatingTokens ? "Counting this server's tools…"
        : TokenEstimate is not { } estimate ? null
        : !estimate.Available
            ? AuthState == McpAuthState.AuthorizationRequired
                ? "Couldn't count this server's tools — it needs a sign-in first. Sign in from the MCP servers dialog, then re-check."
                : "Couldn't reach this server to count its tools — it may be offline, need a sign-in, or its plugin isn't loaded."
        : $"{estimate.ToolCount} tool{(estimate.ToolCount == 1 ? string.Empty : "s")}, ~{McpToolTokenMath.Format(estimate.EstimatedTokens)} tokens (estimate)";

    public McpServerSelectionItemViewModel(string name, bool isProjectLinked = false)
    {
        Name = name;
        IsProjectLinked = isProjectLinked;
    }
}

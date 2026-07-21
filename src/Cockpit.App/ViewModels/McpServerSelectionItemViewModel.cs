using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cockpit.App.ViewModels;

/// <summary>
/// One checkbox row in an MCP-server checklist: a server's name plus whether it is ticked. Used both in the
/// New-session dialog for the per-session selection (#44) and in the profile editor for a profile's saved
/// pre-selection (AC-130). Defaults to checked, matching the pre-#44 behaviour of loading every enabled server.
/// Carries an optional pre-flight tool-token estimate (AC-134) so each row can show roughly what the server adds.
/// </summary>
public partial class McpServerSelectionItemViewModel : ViewModelBase
{
    public string Name { get; }

    [ObservableProperty]
    private bool _isEnabledForSession = true;

    /// <summary>True while the estimate is being enumerated in the background, so the row can show a placeholder.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokenLabel))]
    [NotifyPropertyChangedFor(nameof(TokenTooltip))]
    private bool _isEstimatingTokens;

    /// <summary>The pre-flight tool-token estimate for this server (AC-134), or null before one has been computed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokenLabel))]
    [NotifyPropertyChangedFor(nameof(TokenTooltip))]
    private McpServerToolEstimate? _tokenEstimate;

    /// <summary>
    /// The per-row token figure next to the checkbox: blank before an estimate exists, "…" while counting, "?" when
    /// the server could not be enumerated (unreachable or needs auth), else "~4.2k" (AC-134).
    /// </summary>
    public string TokenLabel =>
        IsEstimatingTokens ? "…"
        : TokenEstimate is not { } estimate ? string.Empty
        : !estimate.Available ? "?"
        : $"~{McpToolTokenMath.Format(estimate.EstimatedTokens)}";

    /// <summary>
    /// Hover text explaining the per-row figure (AC-134) — most usefully the "?": a server counts as unknown when
    /// it could not be reached to list its tools (offline, needs a sign-in, or its plugin is not loaded), which is
    /// otherwise easy to read as a zero. Null when there is nothing worth explaining (no estimate yet).
    /// </summary>
    public string? TokenTooltip =>
        IsEstimatingTokens ? "Counting this server's tools…"
        : TokenEstimate is not { } estimate ? null
        : !estimate.Available ? "Couldn't reach this server to count its tools — it may be offline, need a sign-in, or its plugin isn't loaded."
        : $"{estimate.ToolCount} tool{(estimate.ToolCount == 1 ? string.Empty : "s")}, ~{McpToolTokenMath.Format(estimate.EstimatedTokens)} tokens (estimate)";

    public McpServerSelectionItemViewModel(string name)
    {
        Name = name;
    }
}

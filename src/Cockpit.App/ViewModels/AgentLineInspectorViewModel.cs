using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Abstractions.Agents;

namespace Cockpit.App.ViewModels;

// One attempt on the line, as the operator reads it back (AC-397).
public sealed record AgentLineMessageRow(string When, string From, string To, string Kind, string Outcome, string Body);

// One wake — fired or refused. Refusals are the half a success-only log would hide.
public sealed record AgentLineWakeRow(string When, string From, string To, string Outcome);

// One standing claim, with its age: an old one is the shape an agent that went away without releasing leaves.
public sealed record AgentLineClaimRow(string Resource, string HeldBy, string Age);

// What one sender has spent against the rate limit inside the current window.
public sealed record AgentLineBudgetRow(string PaneId, string Activity, string Spent);

// A pane the cockpit can see that has never called in — the AC-156 shape, made visible rather than left out.
public sealed record AgentLineGapRow(string PaneId, string Note);

// Resolved by the caller rather than through `IWorkspaceAgentGateway`, which is what an earlier revision did and could
// not: that gateway is constructed *from* `CockpitViewModel`, so taking it as a constructor dependency of that same
// view model is a cycle the container follows until the stack runs out.
public sealed record AgentLineDesk(string WorkspaceId, IReadOnlySet<string> PaneIds);

// Read-only by construction and not only by intent: this view model exposes no command that sends, wakes or releases
// anything, and the services it holds are the reading halves of the line's stores (AC-397, AC-34).
public sealed partial class AgentLineInspectorViewModel : ObservableObject
{
    private readonly IAgentNotifyAuditLog? _trail;
    private readonly IAgentResourceClaims? _claims;
    private readonly IAgentLineBudget? _budget;
    private readonly IWorkspaceAgentCoordinator? _roster;

    // How many trail entries one look back reads. Newest first, so this is a recent history and not an archive.
    internal const int MaxTrailEntries = 200;

    // The design-time and no-services shape: every list empty, and a line saying so rather than a blank panel.
    public AgentLineInspectorViewModel()
    {
    }

    public AgentLineInspectorViewModel(
        IAgentNotifyAuditLog trail,
        IAgentResourceClaims claims,
        IAgentLineBudget budget,
        IWorkspaceAgentCoordinator roster)
    {
        _trail = trail;
        _claims = claims;
        _budget = budget;
        _roster = roster;
    }

    // The desk in view, read fresh on every refresh. Assigned by `CockpitViewModel` after construction,
    // the same way the worktrees panel is given its live-session source: the answer changes with the operator's
    // selection, so a value captured once would go stale on the first tab switch.
    public Func<AgentLineDesk?> Desk { get; set; } = static () => null;

    public ObservableCollection<AgentLineMessageRow> Messages { get; } = [];

    public ObservableCollection<AgentLineWakeRow> Wakes { get; } = [];

    public ObservableCollection<AgentLineClaimRow> Claims { get; } = [];

    public ObservableCollection<AgentLineBudgetRow> Budget { get; } = [];

    public ObservableCollection<AgentLineGapRow> Gaps { get; } = [];

    // What the window says when there is nothing to show. Empty must look empty and not broken, and the two reasons
    // for empty — no desk in view, or a quiet desk — are different enough that the operator should be told which.
    [ObservableProperty]
    private string _emptyNote = "Select a session to see the agent line for its desk.";

    // The desk being reported on, so it is never ambiguous which one the numbers belong to.
    [ObservableProperty]
    private string _deskNote = string.Empty;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        Messages.Clear();
        Wakes.Clear();
        Claims.Clear();
        Budget.Clear();
        Gaps.Clear();

        if (_trail is null || _claims is null || _budget is null || _roster is null)
        {
            DeskNote = string.Empty;
            EmptyNote = "The agent line is not running in this window.";
            return;
        }

        if (Desk() is not { PaneIds.Count: > 0 } desk)
        {
            DeskNote = string.Empty;
            EmptyNote = "Select an agent session to see the line for its desk.";
            return;
        }

        DeskNote = $"Desk {desk.WorkspaceId} · {desk.PaneIds.Count} agent session(s)";

        var onThisDesk = desk.PaneIds;
        var now = DateTimeOffset.UtcNow;

        // The trail is app-wide and this window is not, so it is filtered to the desk in view. Filtered on the sender
        // rather than on either end: an entry names a recipient it was refused for reaching, and matching on that
        // would put another desk's pane ids in front of an operator looking at this one.
        var entries = await _trail.ReadRecentAsync(MaxTrailEntries).ConfigureAwait(true);
        foreach (var entry in entries.Where(entry => entry.FromPaneId is { } from && onThisDesk.Contains(from)))
        {
            Messages.Add(new AgentLineMessageRow(
                entry.At.LocalDateTime.ToString("HH:mm:ss"),
                entry.FromPaneId ?? "(unattributed)",
                entry.ToPaneId,
                entry.Kind,
                entry.Outcome.ToString(),
                entry.Body));

            // Every wake that was asked for, including the ones that did not happen. The sender is told, but the
            // sender is not who this record is for: without the refusals an operator sees that agents talked and
            // never that one kept trying to start turns on another's session.
            if (entry.Wake is { } wake)
            {
                Wakes.Add(new AgentLineWakeRow(
                    entry.At.LocalDateTime.ToString("HH:mm:ss"),
                    entry.FromPaneId ?? "(unattributed)",
                    entry.ToPaneId,
                    wake.ToString()));
            }
        }

        foreach (var claim in _claims.List(onThisDesk))
        {
            Claims.Add(new AgentLineClaimRow(
                claim.Resource,
                claim.OwnerPaneId,
                $"{(long)Math.Max(0d, (now - claim.ClaimedAtUtc).TotalMinutes)} min"));
        }

        foreach (var usage in _budget.Usage().Where(usage => onThisDesk.Contains(usage.PaneId)))
        {
            Budget.Add(new AgentLineBudgetRow(
                usage.PaneId,
                usage.Activity.ToString(),
                $"{usage.Used} of {usage.Limit} in the last {usage.Window.TotalSeconds:0}s"));
        }

        foreach (var paneId in onThisDesk.Where(paneId => _roster.LastContactUtc(paneId) is null).Order(StringComparer.Ordinal))
        {
            Gaps.Add(new AgentLineGapRow(
                paneId,
                "On this desk, but has never called a cockpit-agents tool. Either it has not looked yet, the server is not mounted for it, or its MCP injection failed silently."));
        }

        EmptyNote = Messages.Count == 0 && Claims.Count == 0 && Gaps.Count == 0
            ? "Nothing has happened on this desk's agent line yet."
            : string.Empty;
    }
}

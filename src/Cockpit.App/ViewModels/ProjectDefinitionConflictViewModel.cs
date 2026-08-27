using CommunityToolkit.Mvvm.Input;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.App.ViewModels;

// Built once per conflict from three snapshots of the same five fields: what the operator opened the editor with
// (`baseline`), what the operator actually typed (`mine`), and what the source found on the fresh re-read its own
// rejected write already did (`latest`) — never a fourth read just to show this window (AC-247).
public sealed partial class ProjectDefinitionConflictViewModel : ViewModelBase
{
    // Raised when the operator answers: the resolution to retry with, or null when they cancelled (back to
    // editing, nothing written) — the same "null means cancel" idiom every other dialog in this app uses.
    public event Action<ProjectDefinitionConflictResolution?>? CloseRequested;

    // Design-time constructor for the Avalonia previewer.
    public ProjectDefinitionConflictViewModel()
    {
        Rows =
        [
            new ProjectDefinitionConflictRowViewModel("Behaviour", "…use ICqrsSender", "…use ICqrsSender + no AutoMapper", MineChanged: true),
            new ProjectDefinitionConflictRowViewModel("MCP overlay", "+ Playwright", "unchanged", MineChanged: false),
        ];
        HasCollision = true;
    }

    public ProjectDefinitionConflictViewModel(SharedProjectDefinitionEdit mine, SharedProjectBinding baseline, SharedProjectBinding latest)
    {
        Rows = _BuildRows(mine, baseline, latest);
        HasCollision = Rows.Any(row => row.MineChanged);
    }

    // One row per field where the source's own current value moved since the operator's editor opened — a field
    // nobody else touched needs no row: the operator's edit to it, if any, was never in question. In display order,
    // matching ProjectDialogViewModel's own field order top to bottom.
    public IReadOnlyList<ProjectDefinitionConflictRowViewModel> Rows { get; }

    // A conflict caused only by one of those still opens this window (the checksum genuinely no longer matches), but
    // produces zero rows — shown as this note instead of a table with nothing in it, so "Cancel"/"Take theirs" still
    // make sense even though there is nothing here to compare (AC-247).
    public bool HasNoVisibleRows => Rows.Count == 0;

    // Whether at least one row is a genuine collision — the operator's own edit disagrees with what changed remotely on
    // the very same field (AC-247's own edge case: "botsen beide kanten op hetzelfde veld").
    public bool HasCollision { get; }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);

    [RelayCommand]
    private void TakeTheirs() => CloseRequested?.Invoke(new ProjectDefinitionConflictResolution(TakeTheirs: true));

    [RelayCommand]
    private void ApplyMine() => CloseRequested?.Invoke(new ProjectDefinitionConflictResolution(TakeTheirs: false));

    private static IReadOnlyList<ProjectDefinitionConflictRowViewModel> _BuildRows(
        SharedProjectDefinitionEdit mine, SharedProjectBinding baseline, SharedProjectBinding latest)
    {
        var rows = new List<ProjectDefinitionConflictRowViewModel>();

        void Add(string label, string? baselineValue, string? mineValue, string? latestValue)
        {
            // Depot's own value for this field never moved since the operator opened the editor — nothing to
            // reconcile here, whether or not the operator edited it themselves (an uncontested edit needs no row).
            if (string.Equals(baselineValue, latestValue, StringComparison.Ordinal))
            {
                return;
            }

            var mineChanged = !string.Equals(baselineValue, mineValue, StringComparison.Ordinal);
            rows.Add(new ProjectDefinitionConflictRowViewModel(
                label, latestValue is { Length: > 0 } ? latestValue : "(empty)", mineChanged ? mineValue is { Length: > 0 } ? mineValue : "(empty)" : "unchanged", mineChanged));
        }

        Add("Name", baseline.Name, mine.Name, latest.Name);
        Add("Description", baseline.Description, mine.Description, latest.Description);
        Add("Behaviour", baseline.BehaviorPrompt, mine.BehaviorPrompt, latest.BehaviorPrompt);
        Add("Worktree isolation", _BoolText(baseline.IsolateInWorktreeByDefault), _BoolText(mine.IsolateInWorktreeByDefault), _BoolText(latest.IsolateInWorktreeByDefault));
        Add("MCP overlay", _NamesText(baseline.EnabledMcpServerNames), _NamesText(mine.EnabledMcpServerNames), _NamesText(latest.EnabledMcpServerNames));

        return rows;
    }

    private static string _BoolText(bool value) => value ? "on" : "off";

    private static string? _NamesText(IReadOnlyList<string>? names) =>
        names is null ? null : string.Join(", ", names);
}

// One field this conflict names (AC-247) — a row in the table the mockup draws as "Veld | Nu in Depot | Jouw
// versie". `DepotValue` is always shown; `MineValue` reads "unchanged" for a field the operator never touched
// (Depot's fresh value is what will be used for it either way, regardless of which button the operator picks).
public sealed record ProjectDefinitionConflictRowViewModel(string FieldLabel, string DepotValue, string MineValue, bool MineChanged);

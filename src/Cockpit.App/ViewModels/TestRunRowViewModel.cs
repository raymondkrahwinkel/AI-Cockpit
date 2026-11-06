using Cockpit.Core.Abstractions.Shell;

namespace Cockpit.App.ViewModels;

// AC-2028: one run as the test-runs dialog shows it; the dot and the word say what it is doing, the chip says it went through the heavy-run queue.
public sealed class TestRunRowViewModel
{
    public TestRunRowViewModel(TestRunEntry entry, string? sessionName)
    {
        Entry = entry;
        SessionName = string.IsNullOrWhiteSpace(sessionName) ? "—" : sessionName;
    }

    public TestRunEntry Entry { get; }

    public string RunId => Entry.RunId;

    public string Title => Entry.Command;

    public bool IsHeavy => Entry.IsHeavy;

    public bool CanStop => Entry.IsActive && Entry.StopAsync is not null;

    public string SessionName { get; }

    public string? PaneId => Entry.Session;

    public bool HasSession => SessionName != "—";

    public string StatusText => Entry.State switch
    {
        TestRunState.Failed => "Failed",
        _ => Entry.State.ToString(),
    };

    public string StatusBrushKey => Entry.State switch
    {
        TestRunState.Running => "CockpitStatusBusyBrush",
        TestRunState.Queued => "CockpitStatusWaitingBrush",
        TestRunState.Passed => "CockpitStatusDoneBrush",
        TestRunState.Failed => "CockpitStatusErrorBrush",
        _ => "CockpitTextFaintBrush",
    };

    public string Note => Entry.Note;

    public string KindText => Entry.Kind is null ? "—" : Entry.IsHeavy ? $"Heavy · {Entry.Kind}" : "Light, started at once";

    public string WorkingDirectory => Entry.WorkingDirectory ?? "—";

    public string Output => string.IsNullOrWhiteSpace(Entry.Output)
        ? Entry.IsActive ? "Output appears when the run finishes." : "No output."
        : Entry.Output;
}

namespace Cockpit.App.ViewModels;

// AC-2028: one band of the test-runs list — Running, Queued or Finished — hidden while empty.
public sealed class TestRunGroupViewModel(string title, IReadOnlyList<TestRunRowViewModel> runs)
{
    public string Header => $"{title} ({runs.Count})";

    public IReadOnlyList<TestRunRowViewModel> Runs => runs;
}

using Cockpit.Core.Verify;

namespace Cockpit.App.ViewModels;

/// <summary>
/// A single verify runner as the Verify-runners dialog shows it (AC-86): its name, project directory and the
/// command it would run, plus the underlying record so Edit and Remove act on the real runner.
/// </summary>
public sealed class VerifyRunnerRowViewModel(VerifyRunner runner)
{
    public VerifyRunner Runner { get; } = runner;

    public string Label => Runner.Label;

    public string WorkingDirectory => Runner.WorkingDirectory;

    /// <summary>The executable and its arguments as one line, the way the consent prompt shows it before a run.</summary>
    public string CommandLine => string.Join(' ', new[] { Runner.Command }.Concat(Runner.Arguments));

    public string SnapshotPath => Runner.SnapshotPath;
}

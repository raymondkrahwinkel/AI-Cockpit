using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Abstractions.Verify;
using Cockpit.Core.Verify;

namespace Cockpit.App.ViewModels;

/// <summary>
/// The Verify-runners configuration dialog (AC-86): the per-project commands the visual verify loop may run. The
/// operator registers, edits or removes a runner here — a command that renders the UI to a snapshot file the agent
/// is then fed. The agent can only trigger a registered runner, never write one, so this is the only place a verify
/// command is defined.
/// </summary>
public sealed partial class VerifyRunnersViewModel : ObservableObject
{
    private readonly IVerifyRunnerRegistry? _registry;

    // The label of the runner being edited, so a rename removes the old entry rather than leaving a duplicate; null
    // while adding a new runner.
    private string? _editingLabel;

    // Design-time/previewer: one row and an open editor so the dialog renders without a live registry behind it.
    public VerifyRunnersViewModel()
    {
        Runners.Add(new VerifyRunnerRowViewModel(new VerifyRunner(
            "Cockpit", "/home/me/cockpit", "dotnet",
            ["run", "--project", "src/Cockpit.App", "--", "--screenshot", "verify.png", "--scene", "session", "--snapshot", "verify.txt"],
            "/home/me/cockpit/verify.txt", "/home/me/cockpit/verify.png")));
    }

    public VerifyRunnersViewModel(IVerifyRunnerRegistry registry)
    {
        _registry = registry;
    }

    public ObservableCollection<VerifyRunnerRowViewModel> Runners { get; } = [];

    public bool HasRunners => Runners.Count > 0;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string _editLabel = string.Empty;

    [ObservableProperty]
    private string _editWorkingDirectory = string.Empty;

    [ObservableProperty]
    private string _editCommand = string.Empty;

    [ObservableProperty]
    private string _editArguments = string.Empty;

    [ObservableProperty]
    private string _editSnapshotPath = string.Empty;

    [ObservableProperty]
    private string _editScreenshotPath = string.Empty;

    [ObservableProperty]
    private string? _validationError;

    public async Task LoadAsync()
    {
        if (_registry is null)
        {
            return;
        }

        var runners = await _registry.ListAsync();
        Runners.Clear();
        foreach (var runner in runners)
        {
            Runners.Add(new VerifyRunnerRowViewModel(runner));
        }

        OnPropertyChanged(nameof(HasRunners));
    }

    [RelayCommand]
    private void NewRunner()
    {
        _editingLabel = null;
        EditLabel = string.Empty;
        EditWorkingDirectory = string.Empty;
        EditCommand = string.Empty;
        EditArguments = string.Empty;
        EditSnapshotPath = string.Empty;
        EditScreenshotPath = string.Empty;
        ValidationError = null;
        IsEditing = true;
    }

    [RelayCommand]
    private void BeginEdit(VerifyRunnerRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        _editingLabel = row.Runner.Label;
        EditLabel = row.Runner.Label;
        EditWorkingDirectory = row.Runner.WorkingDirectory;
        EditCommand = row.Runner.Command;
        EditArguments = string.Join('\n', row.Runner.Arguments);
        EditSnapshotPath = row.Runner.SnapshotPath;
        EditScreenshotPath = row.Runner.ScreenshotPath ?? string.Empty;
        ValidationError = null;
        IsEditing = true;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
        ValidationError = null;
    }

    // Fills the form with the runner that renders Cockpit itself through the built-in headless screenshotter, so the
    // operator has a working example to keep or adapt rather than a blank technical form. The project directory is
    // left for them — it is the one field only they know.
    [RelayCommand]
    private void FillCockpitExample()
    {
        if (string.IsNullOrWhiteSpace(EditLabel))
        {
            EditLabel = "Cockpit";
        }

        EditCommand = "dotnet";
        EditArguments = string.Join('\n', "run", "--project", "src/Cockpit.App", "--", "--screenshot", "out.png", "--scene", "session", "--snapshot", "out.txt");
        EditSnapshotPath = "out.txt";
        EditScreenshotPath = "out.png";
        ValidationError = null;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_registry is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(EditLabel)
            || string.IsNullOrWhiteSpace(EditWorkingDirectory)
            || string.IsNullOrWhiteSpace(EditCommand)
            || string.IsNullOrWhiteSpace(EditSnapshotPath))
        {
            ValidationError = "Name, project directory, command and snapshot path are all required.";
            return;
        }

        var runner = new VerifyRunner(
            EditLabel.Trim(),
            EditWorkingDirectory.Trim(),
            EditCommand.Trim(),
            _ParseArguments(EditArguments),
            EditSnapshotPath.Trim(),
            string.IsNullOrWhiteSpace(EditScreenshotPath) ? null : EditScreenshotPath.Trim());

        // A rename leaves the old label behind (SaveAsync keys on the new one), so drop it explicitly first.
        if (_editingLabel is { } previous && !string.Equals(previous, runner.Label, StringComparison.OrdinalIgnoreCase))
        {
            await _registry.RemoveAsync(previous);
        }

        await _registry.SaveAsync(runner);
        await LoadAsync();
        IsEditing = false;
    }

    [RelayCommand]
    private async Task RemoveAsync(VerifyRunnerRowViewModel? row)
    {
        if (_registry is null || row is null)
        {
            return;
        }

        await _registry.RemoveAsync(row.Runner.Label);
        await LoadAsync();
    }

    // One argument per line so nothing is re-parsed by a shell; blank lines are ignored.
    private static IReadOnlyList<string> _ParseArguments(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.App.Services;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Backup;
using Cockpit.Core.Backup;

namespace Cockpit.App.ViewModels.Onboarding;

// Drives the wizard's restore step (AC-1280): on a fresh machine, put a backup back instead of answering the very
// questions the archive already holds. What happens *after* a restore is the whole point — see <see cref="TookOver"/>.
public sealed partial class RestoreStepViewModel : ObservableObject
{
    private readonly IBackupService? _backups;
    private readonly IFirstRunWizardStateStore? _stateStore;
    private readonly IAppRestartService? _restart;

    private CancellationTokenSource? _cancellation;

    // All three optional so the previewer and the screenshot scene can stage this page without a container; the
    // real step hands in what the container resolved.
    public RestoreStepViewModel(
        IBackupService? backups = null,
        IFirstRunWizardStateStore? stateStore = null,
        IAppRestartService? restart = null)
    {
        _backups = backups;
        _stateStore = stateStore;
        _restart = restart;
    }

    // True once a restore has landed and the restart is on its way. The startup route reads it to leave its own
    // close handling alone (`FirstRunWizardViewModel.FinishFromStartup`): this wizard is over, and the settings it
    // would otherwise write are the ones that were just put back.
    public bool TookOver { get; private set; }

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _isRunning;

    // Separate from `IsRunning` (AC-1278): the button stays on screen and goes dead the moment stopping stops
    // being free, rather than disappearing and leaving the operator to guess why.
    [ObservableProperty]
    private bool _canStop;

    public void Stop() => _cancellation?.Cancel();

    // Puts the archive back whole — the settings, and every plugin it names. Only a restore that ran to the end
    // takes the wizard over; anything else leaves the operator on this step with the wizard's usual route intact.
    public async Task RestoreAsync(string archivePath)
    {
        if (_backups is not { } backups || _stateStore is null)
        {
            Status = "Restoring is not available in this build, so nothing was done.";

            return;
        }

        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;

        try
        {
            IsRunning = true;
            CanStop = true;
            Status = "Reading the backup…";

            var manifest = await backups.ReadManifestAsync(archivePath, cancellation.Token);
            var options = new RestoreOptions(Settings: true, [.. manifest.Plugins.Keys]);
            var report = await backups.RestoreAsync(archivePath, options, new Progress<RestoreProgress>(_Report), cancellation.Token);

            // A stop is neither success nor failure: it is only offered before the settings are written, so this
            // machine is untouched. Completing the wizard would skip questions nobody answered and a restart would
            // come up on what the operator was trying to replace, so the wizard simply carries on (AC-1280).
            if (report.Stopped)
            {
                Status = "The restore was stopped, so your settings are unchanged." + _AboutThePlugins(report);

                return;
            }

            await _stateStore.MarkCompletedAsync(FirstRunWizardVersion.Current, cancellation.Token);
            TookOver = true;
            Status = "Restored." + _AboutThePlugins(report) + " Restarting to read it.";

            _restart?.Restart();
        }
        catch (Exception exception)
        {
            Status = $"Nothing was restored: {exception.Message}";
        }
        finally
        {
            _cancellation = null;
            IsRunning = false;
            CanStop = false;
        }
    }

    // Passed on as the restore worded it, under a heading that is not a verdict: a plugin that came back on a
    // newer version is worth reading and is not a problem, and one line cannot know which of the two it carries.
    private static string _AboutThePlugins(RestoreReport report) =>
        report.MissingPlugins.Count == 0
            ? string.Empty
            : " About the plugins: "
              + string.Join(", ", report.MissingPlugins.Select(plugin => $"{plugin.Id} ({plugin.Reason})")) + ".";

    private void _Report(RestoreProgress progress)
    {
        // Reports are marshalled onto the UI thread (`Progress<T>`), so one can still arrive after the restore has
        // answered — and a stage line written over the outcome would tell the operator the opposite of what happened.
        if (!IsRunning)
        {
            return;
        }

        Status = progress switch
        {
            { Stage: RestoreStage.Unpacking } => "Unpacking the archive…",
            { Stage: RestoreStage.FetchingPlugins, Total: > 0 } => $"Fetching plugins… {progress.Done} of {progress.Total}.",
            { Stage: RestoreStage.FetchingPlugins } => "Fetching plugins…",
            _ => "Putting the settings back…",
        };

        // Past the write stage a half-written cockpit.json is the risk the staging step exists to prevent, so the
        // offer is withdrawn rather than ignored.
        if (progress.Stage == RestoreStage.Writing)
        {
            CanStop = false;
        }
    }
}

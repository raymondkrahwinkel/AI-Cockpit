using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Abstractions.Clones;

namespace Cockpit.App.ViewModels;

/// <summary>
/// Backs the Clone-from-a-Git-URL dialog (AC-90): the operator pastes a repository URL, and the dialog clones it —
/// through <see cref="IRepositoryCloneManager"/> — into the managed clones area, then closes carrying the local path
/// the New-session dialog then starts the session in. Composes with worktree isolation (AC-85): the clone is a
/// repository root a session can worktree off.
/// </summary>
/// <remarks>
/// The clone runs here rather than fire-and-forget so its outcome is shown in place: a spinner while it runs, and an
/// actionable message that keeps the dialog open on failure (a missing credential helper, a private repo, a bad URL)
/// rather than closing on a repository that is not there. Authentication is the host's own git credential helper —
/// the cockpit never handles a token.
/// </remarks>
public sealed partial class CloneFromGitUrlDialogViewModel : ObservableObject
{
    private readonly IRepositoryCloneManager? _cloneManager;

    /// <summary>Raised when the dialog should close: the local clone path on success, or <see langword="null"/> on cancel.</summary>
    public event Action<string?>? CloseRequested;

    /// <summary>The repository URL to clone — HTTPS, or an SSH remote the host's ssh config already handles.</summary>
    [ObservableProperty]
    private string _url = string.Empty;

    /// <summary>True while the clone is running: the inputs disable and a progress line shows, so a slow clone reads as working, not hung.</summary>
    [ObservableProperty]
    private bool _isCloning;

    /// <summary>The last clone failure, shown in place so the operator can fix it (auth, URL) without losing the dialog. Null when there is nothing to report.</summary>
    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Design-time constructor for the previewer.</summary>
    public CloneFromGitUrlDialogViewModel()
    {
    }

    public CloneFromGitUrlDialogViewModel(IRepositoryCloneManager cloneManager)
    {
        _cloneManager = cloneManager;
    }

    /// <summary>Clone is actionable when a URL is typed and none is already running.</summary>
    public bool CanClone => !IsCloning && !string.IsNullOrWhiteSpace(Url);

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    partial void OnUrlChanged(string value)
    {
        ErrorMessage = null;
        OnPropertyChanged(nameof(CanClone));
        CloneCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsCloningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanClone));
        CloneCommand.NotifyCanExecuteChanged();
    }

    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));

    [RelayCommand(CanExecute = nameof(CanClone))]
    private async Task CloneAsync()
    {
        if (_cloneManager is null)
        {
            return;
        }

        IsCloning = true;
        ErrorMessage = null;
        try
        {
            // Task.Run so the synchronous git spawn never touches the UI thread; the clone can take a while.
            var clone = await Task.Run(() => _cloneManager.CloneAsync(Url.Trim())).ConfigureAwait(true);
            CloseRequested?.Invoke(clone.Path);
        }
        catch (Exception exception)
        {
            // Keep the dialog open with git's own diagnosis (credential helper, SAML SSO, a bad URL) so the operator
            // can act on it — a clone that failed must not close the dialog on a directory that is not there.
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsCloning = false;
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);
}

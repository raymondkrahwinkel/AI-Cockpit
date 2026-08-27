using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.App.ViewModels;

// Loads on open, never blocking the project editor behind it (criterion 6: this runs on its own window, and the load
// itself is a plain awaited `Task`, never a synchronous wait) (AC-502).
public partial class MemorySourceLocationPickerViewModel : ViewModelBase
{
    private readonly Func<CancellationToken, Task<ProjectMemorySourceLocationsResult>> _listLocationsAsync;
    private readonly Func<CancellationToken, Task<bool>>? _signInAsync;

    // The row's own `Reference` at the moment this picker opened (AC-499) — bare, the same shape
    // `ProjectMemorySourceLocation.Value` is in, never a scheme-prefixed `ProjectMemoryRef`.
    private readonly string? _currentValue;

    // Without this, whichever call's result lands last always wins, even if it started first and is answering a stale
    // question — a losing early load's error could stomp a winning later load's list, breaking the "the four states are
    // mutually exclusive" invariant this class documents.
    private int _loadGeneration;

    // Raised when the operator confirms a pick (the location's bare `Value`) or cancels (null).
    public event Action<string?>? CloseRequested;

    // What the picker is choosing from — the source's own `Title`, e.g. "Depot project — Wispslate".
    public string SourceTitle { get; }

    public ObservableCollection<ProjectMemorySourceLocation> Locations { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPick))]
    [NotifyCanExecuteChangedFor(nameof(PickCommand))]
    private ProjectMemorySourceLocation? _selectedLocation;

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private bool _isSigningIn;

    // Whether the source reported it needs a sign-in before it can list anything (criterion 4).
    [ObservableProperty]
    private bool _needsSignIn;

    // Whether this source offers a sign-in at all — false hides the button and leaves the message plain.
    public bool CanSignIn => _signInAsync is not null;

    // Set when the load failed outright (criterion 5) — never left as a bare empty list.
    [ObservableProperty]
    private string? _errorMessage;

    public bool CanPick => SelectedLocation is not null;

    // Bound by the "Current" badge in the list's `DataTemplate` — see `_currentValue`.
    public string? CurrentValue => _currentValue;

    // Design-time constructor for the Avalonia previewer.
    public MemorySourceLocationPickerViewModel()
        : this("Depot project — Acme", _ => Task.FromResult(ProjectMemorySourceLocationsResult.Success([])))
    {
    }

    public MemorySourceLocationPickerViewModel(
        string sourceTitle,
        Func<CancellationToken, Task<ProjectMemorySourceLocationsResult>> listLocationsAsync,
        Func<CancellationToken, Task<bool>>? signInAsync = null,
        string? currentValue = null)
    {
        SourceTitle = sourceTitle;
        _listLocationsAsync = listLocationsAsync;
        _signInAsync = signInAsync;
        _currentValue = currentValue is { Length: > 0 } ? currentValue : null;
    }

    // Runs the source's own listing and settles into exactly one of the states above.
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var generation = ++_loadGeneration;
        IsLoading = true;
        ErrorMessage = null;
        NeedsSignIn = false;

        ProjectMemorySourceLocationsResult? result = null;
        Exception? failure = null;
        try
        {
            result = await _listLocationsAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The dialog was closed mid-load — nothing to show.
            return;
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        // A newer LoadAsync started (Retry/SignIn racing this one) while this call was awaiting — its answer is
        // stale and must not overwrite whatever the newer call already decided or is still deciding.
        if (generation != _loadGeneration)
        {
            return;
        }

        if (failure is not null)
        {
            ErrorMessage = failure.Message;
        }
        else
        {
            switch (result!.Outcome)
            {
                case ProjectMemorySourceLocationsOutcome.Success:
                    Locations.Clear();
                    foreach (var location in result.Locations)
                    {
                        Locations.Add(location);
                    }

                    // AC-499: pre-select the row the operator already has, so opening this list shows where they came
                    // from instead of a blank slate.
                    SelectedLocation = _currentValue is null
                        ? null
                        : Locations.FirstOrDefault(location => string.Equals(location.Value, _currentValue, StringComparison.Ordinal));

                    break;
                case ProjectMemorySourceLocationsOutcome.AuthorizationRequired:
                    NeedsSignIn = true;
                    break;
                default:
                    ErrorMessage = result.Error is { Length: > 0 } error ? error : "Couldn't load the list.";
                    break;
            }
        }

        IsLoading = false;
    }

    [RelayCommand]
    private Task Retry() => LoadAsync();

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignIn()
    {
        if (_signInAsync is null || IsSigningIn)
        {
            return;
        }

        IsSigningIn = true;
        try
        {
            if (await _signInAsync(CancellationToken.None).ConfigureAwait(true))
            {
                await LoadAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            IsSigningIn = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanPick))]
    private void Pick() => CloseRequested?.Invoke(SelectedLocation?.Value);

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);
}

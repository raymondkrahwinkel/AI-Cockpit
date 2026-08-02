using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.App.ViewModels;

/// <summary>
/// Backs the "Choose…" picker for a Memory row whose source can enumerate its own locations (AC-502) — a Depot
/// connection's own projects, say. Loads on open, never blocking the project editor behind it (criterion 6: this
/// runs on its own window, and the load itself is a plain awaited <see cref="Task"/>, never a synchronous wait).
/// <para>
/// Three states beyond "here is the list", each shown instead of an empty list rather than alongside one — an empty
/// list must always mean "this source genuinely has nothing", never "something else is going on" (criteria 4/5):
/// <see cref="NeedsSignIn"/>, <see cref="ErrorMessage"/>, and the plain loading state via <see cref="IsLoading"/>.
/// </para>
/// </summary>
public partial class MemorySourceLocationPickerViewModel : ViewModelBase
{
    private readonly Func<CancellationToken, Task<ProjectMemorySourceLocationsResult>> _listLocationsAsync;
    private readonly Func<CancellationToken, Task<bool>>? _signInAsync;

    /// <summary>
    /// The row's own <c>Reference</c> at the moment this picker opened (AC-499) — bare, the same shape
    /// <see cref="ProjectMemorySourceLocation.Value"/> is in, never a scheme-prefixed <c>ProjectMemoryRef</c>. Compared
    /// ordinal against every loaded location's <see cref="ProjectMemorySourceLocation.Value"/> so the "Current" badge
    /// in the list (bound to <see cref="CurrentValue"/> itself, not to <see cref="SelectedLocation"/>) never moves
    /// off the row the operator actually came in on, even after they click a different one.
    /// </summary>
    private readonly string? _currentValue;

    // Review fix: LoadAsync has no re-entrancy guard of its own — SignIn calls it after a successful sign-in, Retry
    // calls it from the error state, and the window itself fires one on open, so two overlapping calls are
    // reachable (a fast SignIn success racing a slow first load, say). Without this, whichever call's result lands
    // last always wins, even if it started first and is answering a stale question — a losing early load's error
    // could stomp a winning later load's list, breaking the "the four states are mutually exclusive" invariant this
    // class documents. Bumped at the start of every LoadAsync; a call whose generation no longer matches the field
    // when its await returns is stale and writes nothing.
    private int _loadGeneration;

    /// <summary>Raised when the operator confirms a pick (the location's bare <c>Value</c>) or cancels (null).</summary>
    public event Action<string?>? CloseRequested;

    /// <summary>What the picker is choosing from — the source's own <c>Title</c>, e.g. "Depot project — Wispslate".</summary>
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

    /// <summary>Whether the source reported it needs a sign-in before it can list anything (criterion 4).</summary>
    [ObservableProperty]
    private bool _needsSignIn;

    /// <summary>Whether this source offers a sign-in at all — false hides the button and leaves the message plain.</summary>
    public bool CanSignIn => _signInAsync is not null;

    /// <summary>Set when the load failed outright (criterion 5) — never left as a bare empty list.</summary>
    [ObservableProperty]
    private string? _errorMessage;

    public bool CanPick => SelectedLocation is not null;

    /// <summary>Bound by the "Current" badge in the list's <c>DataTemplate</c> — see <see cref="_currentValue"/>.</summary>
    public string? CurrentValue => _currentValue;

    /// <summary>Design-time constructor for the Avalonia previewer.</summary>
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

    /// <summary>Runs the source's own listing and settles into exactly one of the states above. Safe to call again (Retry, after sign-in).</summary>
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

                    // AC-499: pre-select the row the operator already has, so opening this list shows where they
                    // came from instead of a blank slate. Ordinal because Value is an opaque identifier (a slug,
                    // not display text), never culture-compared. Deliberately no match => no selection: a stale or
                    // mistyped Reference must read as "not in this list", not as a fabricated pick of whatever
                    // happens to be first (the same "no selection is honest" rule NeedsSignIn/ErrorMessage follow).
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

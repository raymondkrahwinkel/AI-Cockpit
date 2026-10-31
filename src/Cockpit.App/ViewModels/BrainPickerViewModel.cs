using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.App.ViewModels;

// AC-1992: one row of the brain picker. `Reference` is what an Assistant field stores (`<scheme>:<value>`), null for None.
public sealed record BrainChoice(string? Reference, string Name, string? Detail);

// AC-1992: the one brain picker, for every editor with an Assistant field. It lists the brains of each source that
// has any, on demand, and keeps a stored value it cannot list (legacy text, a removed connection) as a row of its own,
// so opening an editor never changes what is stored. Only a pick does, through `ValueChanged`.
public partial class BrainPickerViewModel : ViewModelBase
{
    public static readonly BrainChoice None = new(null, "None", null);

    private readonly IReadOnlyList<ProjectMemorySourceRegistration> _sources;
    private readonly List<ProjectMemorySourceRegistration> _awaitingSignIn = [];

    // Whichever load answers last would otherwise win, even a stale one a newer Refresh or sign-in overtook.
    private int _loadGeneration;

    private BrainChoice _selected;

    public BrainPickerViewModel(IReadOnlyList<ProjectMemorySourceRegistration> sources, string? current, string hint)
    {
        _sources = [.. sources.Where(source => source.ListBrainsAsync is not null)];
        Hint = hint;
        var stored = current?.Trim();
        _selected = string.IsNullOrEmpty(stored) ? None : new BrainChoice(stored, stored, null);
        _choices = _selected == None ? [None] : [None, _selected];
    }

    // A pick, as the reference to store: null for None.
    public event Action<string?>? ValueChanged;

    // Whether any installed and configured source has brains; without one, the editor keeps its plain text field.
    public bool IsAvailable => _sources.Count > 0;

    // Tells what None means where this picker sits: no brain for a profile, the profile's brain for a project.
    public string Hint { get; }

    public bool HasLoaded { get; private set; }

    [ObservableProperty]
    private IReadOnlyList<BrainChoice> _choices;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _needsSignIn;

    // Set when a source failed to list — said, never shown as an empty list.
    [ObservableProperty]
    private string? _errorMessage;

    // A null the list control writes while its items are replaced is no pick, so it never reaches the stored value.
    public BrainChoice? Selected
    {
        get => _selected;
        set
        {
            if (value is null || !SetProperty(ref _selected, value))
            {
                return;
            }

            ValueChanged?.Invoke(value.Reference);
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var generation = ++_loadGeneration;
        IsLoading = true;
        var listed = new List<BrainChoice>();
        var errors = new List<string>();
        var awaitingSignIn = new List<ProjectMemorySourceRegistration>();
        foreach (var source in _sources)
        {
            var title = string.IsNullOrWhiteSpace(source.InstanceTitle) ? source.Title : source.InstanceTitle;
            ProjectMemorySourceLocationsResult result;
            try
            {
                result = source.ListBrainsAsync is { } list
                    ? await list(cancellationToken).ConfigureAwait(true)
                    : ProjectMemorySourceLocationsResult.Success([]);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                result = ProjectMemorySourceLocationsResult.Failed(exception.Message);
            }

            switch (result.Outcome)
            {
                case ProjectMemorySourceLocationsOutcome.Success:
                    listed.AddRange(result.Locations.Select(brain => new BrainChoice($"{source.Scheme}:{brain.Value}", brain.Name, brain.Detail)));
                    break;
                case ProjectMemorySourceLocationsOutcome.AuthorizationRequired:
                    awaitingSignIn.Add(source);
                    break;
                default:
                    errors.Add($"{title}: {(string.IsNullOrWhiteSpace(result.Error) ? "the brains could not be listed." : result.Error)}");
                    break;
            }
        }

        if (generation != _loadGeneration)
        {
            return;
        }

        _Show(listed, everySourceListed: errors.Count == 0 && awaitingSignIn.Count == 0);
        _awaitingSignIn.Clear();
        _awaitingSignIn.AddRange(awaitingSignIn);
        NeedsSignIn = awaitingSignIn.Count > 0;
        ErrorMessage = errors.Count == 0 ? null : string.Join("\n", errors);
        HasLoaded = true;
        IsLoading = false;
    }

    [RelayCommand]
    private Task Refresh() => LoadAsync();

    [RelayCommand]
    private async Task SignIn()
    {
        foreach (var source in _awaitingSignIn.ToList())
        {
            if (source.SignInAsync is { } signIn)
            {
                await signIn(CancellationToken.None).ConfigureAwait(true);
            }
        }

        await LoadAsync().ConfigureAwait(true);
    }

    // The stored reference keeps its row whatever the list says: matched to a listed brain when one is there, else
    // kept as it was, marked unavailable only when every source answered and none listed it.
    private void _Show(List<BrainChoice> listed, bool everySourceListed)
    {
        var current = _selected.Reference;
        var match = current is null
            ? None
            : listed.FirstOrDefault(choice => string.Equals(choice.Reference, current, StringComparison.OrdinalIgnoreCase))
                ?? new BrainChoice(current, current, everySourceListed ? "Not available here" : null);
        Choices = listed.Contains(match) || match == None ? [None, .. listed] : [None, match, .. listed];
        _selected = match;
        OnPropertyChanged(nameof(Selected));
    }
}

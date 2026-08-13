using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Mentions;

namespace Cockpit.App.ViewModels;

// Backs the AC-740 @-mention popup, modeled on CommandPaletteDialogViewModel's Query/Visible/Move/Accept shape.
// Shared by both composers. The working directory is read lazily through `workingDirectory` on every '@' rather
// than captured once — the Assistant-chat's session doesn't exist until the first message starts it.
public partial class MentionPickerViewModel : ViewModelBase
{
    public const int MaxMatches = 15;

    private readonly Func<CancellationToken, Task<IReadOnlyList<string>>> _fileSource;
    private readonly Func<string?> _workingDirectory;

    private IReadOnlyList<string>? _candidates;
    private CancellationTokenSource? _loadCts;
    private int _suppressedTokenStart = -1;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private MentionMatch? _selected;

    public ObservableCollection<MentionMatch> Matches { get; } = [];

    // The index of the '@' that opened the picker, for splicing an accepted/typed-through mention back into the
    // composer's text. -1 while closed.
    public int TokenStart { get; private set; } = -1;

    // Design-time constructor for the previewer.
    public MentionPickerViewModel()
        : this(_ => Task.FromResult<IReadOnlyList<string>>([]), () => null)
    {
    }

    public MentionPickerViewModel(
        Func<CancellationToken, Task<IReadOnlyList<string>>> fileSource,
        Func<string?> workingDirectory)
    {
        _fileSource = fileSource;
        _workingDirectory = workingDirectory;
    }

    // Re-evaluates the mention token under the caret and opens/updates/closes the picker accordingly. Call this
    // only on caret-driven typing — never on a programmatic text mutation (voice input, Up-recall, pasting a
    // block), which would open the picker for text the operator never typed a caret through.
    public void OnTextChanged(string text, int caretIndex)
    {
        if (MentionQuery.From(text, caretIndex) is not { } token || _workingDirectory() is null)
        {
            _Reset(clearSuppression: true);
            return;
        }

        if (token.Start == _suppressedTokenStart)
        {
            // Esc dismissed the picker for this exact token; stay closed until a new '@' starts a new one.
            return;
        }

        _suppressedTokenStart = -1;
        TokenStart = token.Start;

        // Re-ranking hangs off OnQueryChanged alone. Filtering here as well ranked every keystroke twice, and a
        // keystroke that leaves the token's text alone (a caret move, a re-evaluation) now costs nothing at all.
        Query = token.Query;
        if (!IsOpen)
        {
            IsOpen = true;
            _ = _LoadAsync();
        }
    }

    // Moves the selection up/down, clamped to the list — the picker's arrow-key handling.
    public void Move(int delta)
    {
        if (Matches.Count == 0)
        {
            return;
        }

        var index = Selected is null ? 0 : Matches.IndexOf(Selected);
        Selected = Matches[Math.Clamp(index + delta, 0, Matches.Count - 1)];
    }

    // Accepts the current selection and closes the picker, or returns null if nothing is selected.
    public MentionAcceptance? Accept()
    {
        if (Selected is not { } chosen)
        {
            return null;
        }

        var acceptance = new MentionAcceptance(TokenStart, chosen.Path);
        _Reset(clearSuppression: true);
        return acceptance;
    }

    // Closes the picker on Esc and remembers the token, so further typing inside the same @-run doesn't reopen
    // it — only a fresh '@' does.
    public void Dismiss()
    {
        _suppressedTokenStart = TokenStart;
        _Reset(clearSuppression: false);
    }

    // Closes the picker unconditionally — used when the composer itself closes the door (message sent, session
    // torn down). Clears the dismiss-suppression too, since there is no token left to suppress.
    public void Close() => _Reset(clearSuppression: true);

    partial void OnQueryChanged(string value) => _ApplyFilter();

    private void _Reset(bool clearSuppression)
    {
        if (clearSuppression)
        {
            _suppressedTokenStart = -1;
        }

        _loadCts?.Cancel();
        _loadCts = null;
        _candidates = null;
        IsOpen = false;
        IsLoading = false;
        TokenStart = -1;
        Matches.Clear();
        Selected = null;
    }

    private async Task _LoadAsync()
    {
        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        IsLoading = true;
        try
        {
            var candidates = await _fileSource(cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            _candidates = candidates;
            _ApplyFilter();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer load, or the picker closed while the fetch was in flight.
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                IsLoading = false;
            }
        }
    }

    private void _ApplyFilter()
    {
        var ranked = _candidates is { } candidates
            ? MentionMatcher.Rank(candidates, Query, MaxMatches)
            : [];

        // Rewritten in place rather than cleared and refilled: Matches is bound to the popup's ListBox, and a
        // Clear is a reset that throws away and rebuilds every container — on every keystroke, while the operator
        // is typing into the box right underneath it. A row whose path is unchanged now raises nothing at all.
        for (var i = 0; i < ranked.Count; i++)
        {
            if (i == Matches.Count)
            {
                Matches.Add(new MentionMatch(ranked[i]));
            }
            else if (!string.Equals(Matches[i].Path, ranked[i], StringComparison.Ordinal))
            {
                Matches[i] = new MentionMatch(ranked[i]);
            }
        }

        for (var i = Matches.Count - 1; i >= ranked.Count; i--)
        {
            Matches.RemoveAt(i);
        }

        Selected = Matches.Count > 0 ? Matches[0] : null;
    }

}

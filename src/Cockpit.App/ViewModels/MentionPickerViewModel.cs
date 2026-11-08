using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Abstractions.Mentions;
using Cockpit.Core.Mentions;

namespace Cockpit.App.ViewModels;

// Backs the @-mention list shared by every chat window: groups from the IMentionSource set, one flat Matches list
// with heading rows, the selection (headings are skipped), and the tokens that were accepted into the text.
public partial class MentionPickerViewModel : ViewModelBase
{
    public const int MaxMatches = 15;

    public static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(2);

    private readonly IReadOnlyList<IMentionSource> _sources;
    private readonly Func<MentionContext> _context;
    private readonly Dictionary<string, MentionItem> _accepted = [];

    private IReadOnlyList<MentionGroup>? _groups;
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

    [ObservableProperty]
    private IReadOnlyList<MentionToken> _tokens = [];

    // The first project token in text order; the window decides whether it sets the context or is a mention.
    [ObservableProperty]
    private MentionToken? _firstProjectToken;

    // Flat list: a heading row precedes the rows of its group.
    public ObservableCollection<MentionMatch> Matches { get; } = [];

    public bool HasMatches => Matches.Any(match => match.IsItem);

    // The index of the '@' that opened the list, -1 while closed.
    public int TokenStart { get; private set; } = -1;

    // Design-time constructor for the previewer.
    public MentionPickerViewModel()
        : this([], () => new MentionContext(MentionSurface.Home, null))
    {
    }

    public MentionPickerViewModel(IEnumerable<IMentionSource> sources, Func<MentionContext> context)
    {
        _sources = sources.OrderBy(source => source.Order).ToList();
        _context = context;
    }

    // Re-evaluates the token under the caret; call it on caret-driven typing only, never on a programmatic text change.
    public void OnTextChanged(string text, int caretIndex)
    {
        SyncTokens(text);
        if (MentionQuery.From(text, caretIndex) is not { } token)
        {
            _Reset(clearSuppression: true);
            return;
        }

        if (token.Start == _suppressedTokenStart)
        {
            // Esc dismissed the list for this exact token; stay closed until a new '@' starts a new one.
            return;
        }

        _suppressedTokenStart = -1;
        TokenStart = token.Start;

        // Ranking hangs off OnQueryChanged alone, so a keystroke that leaves the token text alone costs nothing.
        Query = token.Query;
        if (!IsOpen)
        {
            IsOpen = true;
            _ = _LoadAsync();
        }
    }

    // Looks the accepted tokens up again by their exact text, longest first, so names with spaces survive.
    public void SyncTokens(string text)
    {
        var found = new List<MentionToken>();
        foreach (var (insert, item) in _accepted.OrderByDescending(pair => pair.Key.Length).ToList())
        {
            var tokenText = "@" + insert;
            var from = 0;
            var any = false;
            while (text.IndexOf(tokenText, from, StringComparison.Ordinal) is var at and >= 0)
            {
                from = at + tokenText.Length;
                var end = at + tokenText.Length;
                var bounded = (at == 0 || char.IsWhiteSpace(text[at - 1])) && (end == text.Length || char.IsWhiteSpace(text[end]));
                if (bounded && !found.Any(other => at < other.Start + other.Length && other.Start < end))
                {
                    found.Add(new MentionToken(item, at, tokenText.Length));
                    any = true;
                }
            }

            if (!any)
            {
                _accepted.Remove(insert);
            }
        }

        found.Sort((a, b) => a.Start.CompareTo(b.Start));
        if (!found.SequenceEqual(Tokens))
        {
            Tokens = found;
            FirstProjectToken = found.FirstOrDefault(token => token.Item.Kind == MentionKind.Project);
        }
    }

    // Moves the selection over the item rows only; the headings are skipped.
    public void Move(int delta)
    {
        var items = Matches.Where(match => match.IsItem).ToList();
        if (items.Count == 0)
        {
            return;
        }

        var index = Selected is null ? 0 : Math.Max(items.IndexOf(Selected), 0);
        Selected = items[Math.Clamp(index + delta, 0, items.Count - 1)];
    }

    // Accepts the current selection and closes the list, or returns null if nothing is selected.
    public MentionAcceptance? Accept()
    {
        if (Selected is not { Item: { } item })
        {
            return null;
        }

        var acceptance = new MentionAcceptance(TokenStart, item);
        _accepted[item.InsertText] = item;
        _Reset(clearSuppression: true);
        return acceptance;
    }

    // Closes on Esc and remembers the token, so typing on inside the same @-run does not reopen it; only a fresh '@' does.
    public void Dismiss()
    {
        _suppressedTokenStart = TokenStart;
        _Reset(clearSuppression: false);
    }

    // Closes unconditionally (message sent, session torn down); there is no token left to suppress.
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
        _groups = null;
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
            var context = _context();
            var loaded = await Task.WhenAll(_sources.Select(source => _LoadOneAsync(source, context, cts.Token)));
            if (cts.IsCancellationRequested)
            {
                return;
            }

            _groups = loaded.SelectMany(groups => groups).OrderBy(group => _KindRank(context.Surface, group)).ToList();
            _ApplyFilter();
        }
        catch (OperationCanceledException)
        {
            // The list closed while the sources were still loading.
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                IsLoading = false;
            }
        }
    }

    // A source that fails or is slow drops out alone; the other groups still show.
    private static async Task<IReadOnlyList<MentionGroup>> _LoadOneAsync(IMentionSource source, MentionContext context, CancellationToken cancellationToken)
    {
        try
        {
            return await source.GetAsync(context, cancellationToken).WaitAsync(SourceTimeout, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return [];
        }
    }

    // OrderBy is stable, so groups of one kind keep their source order. A session reads files first, elsewhere projects lead.
    private static int _KindRank(MentionSurface surface, MentionGroup group) =>
        (group.Items.FirstOrDefault()?.Kind, surface) switch
        {
            (MentionKind.File, MentionSurface.Session) => 0,
            (MentionKind.Project, MentionSurface.Session) => 1,
            (MentionKind.Project, _) => 0,
            (MentionKind.File, _) => 1,
            _ => 2,
        };

    private void _ApplyFilter()
    {
        var ranked = new List<MentionMatch>();
        foreach (var group in _groups ?? [])
        {
            var items = _Rank(group.Items, Query);
            if (items.Count == 0)
            {
                continue;
            }

            ranked.Add(MentionMatch.ForHeading(group));
            ranked.AddRange(items.Select(item => MentionMatch.ForItem(item, Query)));
        }

        // Rewritten in place: Matches is bound to the list, and a Clear would rebuild every container per keystroke.
        for (var i = 0; i < ranked.Count; i++)
        {
            if (i == Matches.Count)
            {
                Matches.Add(ranked[i]);
            }
            else if (Matches[i] != ranked[i])
            {
                Matches[i] = ranked[i];
            }
        }

        for (var i = Matches.Count - 1; i >= ranked.Count; i--)
        {
            Matches.RemoveAt(i);
        }

        Selected = Matches.FirstOrDefault(match => match.IsItem);
        OnPropertyChanged(nameof(HasMatches));
    }

    private static IReadOnlyList<MentionItem> _Rank(IReadOnlyList<MentionItem> items, string query)
    {
        if (items.Count > 0 && items[0].Kind == MentionKind.File)
        {
            var byPath = items.GroupBy(item => item.InsertText).ToDictionary(g => g.Key, g => g.First());
            return MentionMatcher.Rank(byPath.Keys.ToList(), query, MaxMatches).Select(path => byPath[path]).ToList();
        }

        return items
            .Where(item => query.Length == 0
                || item.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.SourceLine.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => query.Length > 0 && item.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(item => item.LastActivity ?? DateTimeOffset.MinValue)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

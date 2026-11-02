using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cockpit.App.ViewModels;

// Backs the Agents palette (AC-1933): the Agents panel's cards and filters in a compact kanban overlay. The search
// text and the Waiting-for-you filter are the panel's own (`Agents`), so both stands show one filter state; what the
// palette adds is the keyboard selection — arrows, Tab between columns, Enter to open, Ctrl+Enter to open beside.
public sealed partial class AgentsPaletteViewModel : ObservableObject
{
    [ObservableProperty]
    private AgentOverviewCardViewModel? _selected;

    public AgentsPaletteViewModel(AgentsOverviewViewModel agents)
    {
        Agents = agents;
        foreach (var column in agents.Columns)
        {
            column.Cards.CollectionChanged += _OnCardsChanged;
        }
        _Select(_Cards.FirstOrDefault());
    }

    private bool _closed;

    public AgentsOverviewViewModel Agents { get; }
    public IReadOnlyList<AgentsOverviewColumnViewModel> Columns => Agents.Columns;

    // The action the operator picked, run by the caller once the palette has closed (like the command palette).
    public Func<Task>? Chosen { get; private set; }

    public event Action? CloseRequested;

    private List<AgentOverviewCardViewModel> _Cards => [.. Columns.SelectMany(column => column.Cards)];

    private void _OnCardsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (Selected is null || !_Cards.Contains(Selected))
        {
            _Select(_Cards.FirstOrDefault());
        }
    }

    private void _Select(AgentOverviewCardViewModel? card)
    {
        if (Selected is not null)
        {
            Selected.IsPicked = false;
        }
        Selected = card;
        if (card is not null)
        {
            card.IsPicked = true;
        }
    }

    public void Select(AgentOverviewCardViewModel card) => _Select(card);

    // Up/Down: the previous or next card, clamped to the list.
    public void Move(int delta)
    {
        var cards = _Cards;
        if (cards.Count > 0)
        {
            _Select(cards[Math.Clamp((Selected is null ? 0 : cards.IndexOf(Selected)) + delta, 0, cards.Count - 1)]);
        }
    }

    // Tab/Shift+Tab: the same row in the next column that has cards, wrapping around.
    public void MoveColumn(int delta)
    {
        if (Selected is null)
        {
            _Select(_Cards.FirstOrDefault());
            return;
        }
        var count = Columns.Count;
        var from = Columns.ToList().FindIndex(column => column.Cards.Contains(Selected));
        var row = Columns[from].Cards.IndexOf(Selected);
        for (var step = 1; step < count; step++)
        {
            var target = Columns[((from + (delta * step)) % count + count) % count];
            if (target.Cards.Count > 0)
            {
                _Select(target.Cards[Math.Min(row, target.Cards.Count - 1)]);
                return;
            }
        }
    }

    public void Open(bool beside)
    {
        if (Selected is { } card)
        {
            Chosen = () => Agents.OpenAsync(card, beside);
            Close();
        }
    }

    // Idempotent: the Esc/Enter paths call it, and so does the dialog's Closed event for the X and Alt+F4.
    public void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        foreach (var column in Columns)
        {
            column.Cards.CollectionChanged -= _OnCardsChanged;
        }
        if (Selected is not null)
        {
            Selected.IsPicked = false;
        }
        CloseRequested?.Invoke();
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.App.Controls;

// Assistant threads (§ 9.6): the row's grip. One way in for mouse and keyboard: drag it onto a thread in the rail, or
// click it, press Enter on it or M on the row, for the Move to project menu.
public partial class TranscriptRowView
{
    // What a dragged row carries; the drop reads the row off the chat itself, this only says it is one.
    public static readonly DataFormat<string> RowDragFormat = DataFormat.CreateInProcessFormat<string>("cockpit/transcript-row");

    // When a press on the grip last ended without a drag and opened the menu itself; the Button's own Click that may
    // follow the same release must not open it a second time.
    private DateTimeOffset _gripPressHandledAt;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.M && e.KeyModifiers == KeyModifiers.None && e.Source is not TextBox && _Grip() is { } grip)
        {
            _OpenMoveMenu(grip);
            e.Handled = true;
        }
    }

    private Button? _Grip() => this.GetVisualDescendants().OfType<Button>()
        .FirstOrDefault(button => button.Classes.Contains("threadGrip") && button.IsEffectivelyVisible);

    // Enter or Space on the grip, which never passes through the press below.
    private void _OnGripClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button grip && DateTimeOffset.UtcNow - _gripPressHandledAt > TimeSpan.FromMilliseconds(500))
        {
            _OpenMoveMenu(grip);
        }
    }

    // The NodePicker way: the press starts the drag, and a press that never became one comes back None — a click.
    private async void _OnGripPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source
            || (source as Button ?? source.FindAncestorOfType<Button>()) is not { } grip
            || !grip.Classes.Contains("threadGrip")
            || grip.DataContext is not TranscriptEntryViewModel row
            || !e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Handled = true;
        var chat = this.FindAncestorOfType<AssistantChatView>()?.DataContext as AssistantChatViewModel;
        row.IsBeingDragged = true;
        if (chat is not null)
        {
            chat.DraggingRow = row;
        }

        DragDropEffects effect;
        try
        {
            using var data = new DataTransfer();
            data.Add(DataTransferItem.Create(RowDragFormat, row.Id));
            effect = await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move | DragDropEffects.Copy);
        }
        finally
        {
            row.IsBeingDragged = false;
            if (chat is not null)
            {
                chat.DraggingRow = null;
            }
        }

        // Nothing was ever dragged over the chat: the press was a click (a drag let go somewhere else was not).
        if (effect == DragDropEffects.None && chat?.RowDragTravelled != true)
        {
            _gripPressHandledAt = DateTimeOffset.UtcNow;
            _OpenMoveMenu(grip);
        }
    }

    // "Move to X" per project, "Move to General (no project)", and under a rule "Also add to X" (§ 9.6).
    private void _OpenMoveMenu(Control grip)
    {
        if (grip.DataContext is not TranscriptEntryViewModel { Session: { } session } row)
        {
            return;
        }

        var items = new List<Control>();
        foreach (var (id, name) in session.ThreadTargets.Where(target => row.ThreadProjectIds is not [var only] || only != target.Id))
        {
            items.Add(_MoveItem($"Move to {name}", () => session.MoveToThread(row, id, add: false)));
        }

        if (!row.IsGeneral)
        {
            items.Add(_MoveItem("Move to General (no project)", () => session.MoveToThread(row, null, add: false)));
        }

        var adds = session.ThreadTargets.Where(target => !row.ThreadProjectIds.Contains(target.Id)).ToList();
        if (!row.IsGeneral && adds.Count > 0)
        {
            items.Add(new Separator());
            items.AddRange(adds.Select(target => (Control)_MoveItem($"Also add to {target.Name}", () => session.MoveToThread(row, target.Id, add: true))));
        }

        var menu = new MenuFlyout { ItemsSource = items, Placement = PlacementMode.BottomEdgeAlignedLeft };
        menu.Closed += (_, _) => grip.Focus(NavigationMethod.Tab);
        menu.ShowAt(grip);
    }

    private static MenuItem _MoveItem(string header, Action move)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => move();
        return item;
    }
}

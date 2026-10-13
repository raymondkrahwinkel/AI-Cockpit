using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

// AC-1532: the sheet over the Simple stand's main area. Esc and × close it; Enter is Continue on the selected row.
public partial class EarlierConversationsSheet : UserControl
{
    public EarlierConversationsSheet()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, _OnKeyDown, RoutingStrategies.Tunnel);
    }

    private EarlierConversationsViewModel? Sheet => DataContext as EarlierConversationsViewModel;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && IsVisible)
        {
            Dispatcher.UIThread.Post(() => EarlierSearch.Focus());
        }
    }

    private void _OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Sheet is not { } sheet)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            sheet.CloseCommand.Execute(null);
        }
        else if (e.Key == Key.Enter && sheet.Selected is not null)
        {
            e.Handled = true;
            sheet.ContinueCommand.Execute(sheet.Selected);
        }
    }

    private void _OnBackdropPressed(object? sender, PointerPressedEventArgs e) => Sheet?.CloseCommand.Execute(null);
}

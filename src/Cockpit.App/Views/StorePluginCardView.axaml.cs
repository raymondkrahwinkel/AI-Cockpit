using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

/// <summary>
/// One plugin card in the store dialog (#62) — reused as-is in the main grid and the Discover page's
/// Featured/Recently-added rails, so its "Install"/"Update" button and the click-anywhere-to-open-details
/// behaviour only live in one place. Its own <see cref="Window.DataContext"/> is the row
/// (<see cref="StorePluginRowViewModel"/>); it reaches the dialog's <see cref="PluginStoreDialogViewModel"/>
/// through the owning window rather than a passed-in reference, since it is instantiated per catalogue
/// row by an <c>ItemsControl</c> template, not directly.
/// </summary>
public partial class StorePluginCardView : UserControl
{
    public StorePluginCardView()
    {
        InitializeComponent();
    }

    // A click on the card's own buttons must not also open the detail panel — bail out whenever the
    // press originated on (or inside) a Button, mirroring CockpitWindowChrome's title-bar drag handler.
    private void OnCardPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Control source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            return;
        }

        if (DataContext is not StorePluginRowViewModel row)
        {
            return;
        }

        if (this.FindAncestorOfType<Window>()?.DataContext is PluginStoreDialogViewModel dialogViewModel)
        {
            dialogViewModel.ShowDetailsCommand.Execute(row);
        }
    }
}

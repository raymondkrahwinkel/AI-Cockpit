using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

// #62: one plugin card, reused as-is in the main grid and the Discover rails so Install/Update and
// click-to-open-details live in one place. Reaches PluginStoreDialogViewModel via the owning
// window rather than a passed-in reference, since an ItemsControl template instantiates it.
public partial class StorePluginCardView : UserControl
{
    public StorePluginCardView()
    {
        InitializeComponent();
    }

    // A click on the card's own buttons must not also open the detail panel — bail out whenever
    // the press originated inside a Button (mirrors CockpitWindowChrome's title-bar drag handler).
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

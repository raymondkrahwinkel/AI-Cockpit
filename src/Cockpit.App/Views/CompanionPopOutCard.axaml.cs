using Avalonia.Controls;

namespace Cockpit.App.Views;

// The card of a pop-out: a dense panel with room around it for its shadow; tall content scrolls.
public partial class CompanionPopOutCard : UserControl
{
    // Room around the card for its shadow, in device-independent pixels.
    internal const int ShadowMargin = 16;

    public CompanionPopOutCard()
    {
        InitializeComponent();
    }

    public Control? Body
    {
        get => Scroller.Content as Control;
        set => Scroller.Content = value;
    }
}

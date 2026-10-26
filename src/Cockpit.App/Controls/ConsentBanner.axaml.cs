using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Controls;

// The inline consent surface (#AC-47): shows a `ViewModels.ConsentPromptViewModel` as an Approve/Deny
// banner in the pane chrome. Hosted per session tile in `CockpitView`, bound to the pane's
// `ViewModels.SessionPanelViewModel.PendingConsent` and hidden while there is none.
public partial class ConsentBanner : UserControl
{
    public ConsentBanner() => AvaloniaXamlLoader.Load(this);

    // AC-1714: a card with choices stands over the whole window, so it takes focus, and Escape on its host then denies.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is ConsentPromptViewModel { HasChoices: true })
        {
            Focusable = true;
            Focus();
        }
    }
}

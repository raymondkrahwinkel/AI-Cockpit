using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Cockpit.App.Controls;

// The inline consent surface (#AC-47): shows a `ViewModels.ConsentPromptViewModel` as an Approve/Deny
// banner in the pane chrome. Hosted per session tile in `CockpitView`, bound to the pane's
// `ViewModels.SessionPanelViewModel.PendingConsent` and hidden while there is none.
public partial class ConsentBanner : UserControl
{
    public ConsentBanner() => AvaloniaXamlLoader.Load(this);
}

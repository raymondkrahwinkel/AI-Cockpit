using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Cockpit.App.Controls;

/// <summary>
/// The inline consent surface (#AC-47): shows a <see cref="ViewModels.ConsentPromptViewModel"/> as an Approve/Deny
/// banner in the pane chrome. Hosted per session tile in <c>CockpitView</c>, bound to the pane's
/// <see cref="ViewModels.SessionPanelViewModel.PendingConsent"/> and hidden while there is none.
/// </summary>
public partial class ConsentBanner : UserControl
{
    public ConsentBanner() => AvaloniaXamlLoader.Load(this);
}

using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Cockpit.App.Controls;

/// <summary>
/// Hosts the consent banner for a session pane (#AC-47): bound to the pane's
/// <see cref="ViewModels.SessionPanelViewModel.PendingConsent"/> and hidden when there is none. Included once in
/// each pane view (SessionView, TtyView) so the wrapper is not duplicated across the two session kinds.
/// </summary>
public partial class ConsentBannerHost : UserControl
{
    public ConsentBannerHost() => AvaloniaXamlLoader.Load(this);
}

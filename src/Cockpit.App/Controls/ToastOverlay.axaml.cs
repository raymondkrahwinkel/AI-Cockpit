using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Cockpit.App.Controls;

/// <summary>
/// The cockpit's toasts, as a control that can be placed anywhere (#61). Extracted from <c>CockpitView</c> so a
/// plugin's own window can show them too: a toast raised from inside one appeared on the window behind it, which
/// while that window covered the cockpit was to say nowhere.
/// </summary>
public partial class ToastOverlay : UserControl
{
    public ToastOverlay() => AvaloniaXamlLoader.Load(this);
}

using Avalonia.Controls;
using Avalonia.Input;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

public partial class CockpitView : UserControl
{
    public CockpitView()
    {
        InitializeComponent();
    }

    /// <summary>Sidebar item click → select that session. Plain event handler (not a command) since the
    /// clicked session is the DataContext of the <see cref="Border"/> raising the event, not the item passed
    /// as a bindable CommandParameter — simplest wiring for a whole-row click target.</summary>
    private void OnSessionItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border { DataContext: ClaudeSessionViewModel session } && DataContext is CockpitViewModel cockpit)
        {
            cockpit.SelectSessionCommand.Execute(session);
        }
    }
}

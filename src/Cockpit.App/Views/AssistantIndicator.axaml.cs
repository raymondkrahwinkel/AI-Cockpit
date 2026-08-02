using Avalonia.Controls;

namespace Cockpit.App.Views;

// The assistant indicator (AC-543) — a plain `UserControl` over
// `ViewModels.AssistantIndicatorViewModel`, deliberately without a constructor argument or a
// reference back to a host: whatever embeds it (the sidebar today, the companion window per AC-238) supplies
// its own view model instance and feeds it state, so this file never has to know which one it is in.
public partial class AssistantIndicator : UserControl
{
    public AssistantIndicator()
    {
        InitializeComponent();
    }
}

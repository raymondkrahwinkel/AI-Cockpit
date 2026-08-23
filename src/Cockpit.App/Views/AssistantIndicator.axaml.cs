using Avalonia.Controls;

namespace Cockpit.App.Views;

// AC-543: plain UserControl over AssistantIndicatorViewModel, deliberately without a
// constructor arg or host reference — whatever embeds it (sidebar, AC-238 companion window)
// supplies its own view model instance, so this file never has to know which one it is in.
public partial class AssistantIndicator : UserControl
{
    public AssistantIndicator()
    {
        InitializeComponent();
    }
}

using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Cockpit.App.Controls;

// AC-1600: the detail pane beside the Projects list. Its data context is the selected `ProjectListItemViewModel`.
public partial class ProjectDetailPane : UserControl
{
    public ProjectDetailPane()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}

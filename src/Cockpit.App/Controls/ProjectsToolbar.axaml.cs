using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Cockpit.App.Controls;

// AC-1600: the Projects workspace's toolbar. Its data context is the `ProjectsViewModel`.
public partial class ProjectsToolbar : UserControl
{
    // The mockup drops the count below this toolbar width (`@media (max-width: 1300px)`).
    private const double CountMinWidth = 1300;

    public ProjectsToolbar()
    {
        InitializeComponent();
        SizeChanged += (_, args) => this.FindControl<TextBlock>("CountText")!.IsVisible = args.NewSize.Width > CountMinWidth;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}

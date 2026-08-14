using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Cockpit.App.Controls;

// One project drawn as a wide row — the List layout of the Projects page (AC-772). Same data, same actions and same
// wording as `ProjectCardView`; only the shape differs.
public partial class ProjectRowView : UserControl
{
    public ProjectRowView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}

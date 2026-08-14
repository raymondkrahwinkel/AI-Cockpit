using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Cockpit.App.Controls;

// One project drawn as a card, shared by every surface that lists projects (AC-772). Everything it needs comes from
// the `ProjectCardViewModel` it is given — including the commands, see `ProjectCardActions` — so it renders the same
// in the Projects workspace and in the Manage-projects window without either one binding it up.
public partial class ProjectCardView : UserControl
{
    public ProjectCardView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}

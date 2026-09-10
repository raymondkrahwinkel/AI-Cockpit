using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Cockpit.App.Controls;

// One project drawn as a card, shared by every surface that lists projects (AC-772). Everything it needs comes from
// the `ProjectCardViewModel` it is given — including the commands, see `ProjectCardActions` — so it renders the same
// in the Projects workspace and in the Manage-projects window without either one binding it up.
public partial class ProjectCardView : UserControl
{
    // AC-1304: what pressing one of this project's jobs does, said by the host rather than decided here — the
    // Projects workspace opens the New-session dialog (AC-491), the Simple stand's start screen starts it
    // straight away. Which applies is the caller's intent, not a fact about the card. Runs with a `ProjectJobChoice`.
    public static readonly StyledProperty<ICommand?> JobCommandProperty =
        AvaloniaProperty.Register<ProjectCardView, ICommand?>(nameof(JobCommand));

    public ICommand? JobCommand
    {
        get => GetValue(JobCommandProperty);
        set => SetValue(JobCommandProperty, value);
    }

    public ProjectCardView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}

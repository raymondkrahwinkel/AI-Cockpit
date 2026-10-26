using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Cockpit.App.Controls;

// AC-1600: the heading of one Projects section. The commands come from the host, which owns the `ProjectsViewModel`.
public partial class ProjectSectionHeader : UserControl
{
    public static readonly StyledProperty<ICommand?> ToggleCommandProperty =
        AvaloniaProperty.Register<ProjectSectionHeader, ICommand?>(nameof(ToggleCommand));

    public static readonly StyledProperty<ICommand?> TryAgainCommandProperty =
        AvaloniaProperty.Register<ProjectSectionHeader, ICommand?>(nameof(TryAgainCommand));

    public ProjectSectionHeader()
    {
        InitializeComponent();
    }

    public ICommand? ToggleCommand
    {
        get => GetValue(ToggleCommandProperty);
        set => SetValue(ToggleCommandProperty, value);
    }

    public ICommand? TryAgainCommand
    {
        get => GetValue(TryAgainCommandProperty);
        set => SetValue(TryAgainCommandProperty, value);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}

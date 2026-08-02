using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace Cockpit.App.Controls;

// A project's logo in the shape every surface shows it in (AC-162): a rounded well holding the stored image, or
// the project's initial while it has none. One control because the overview's cards, the manager's rows and the
// editor's preview all draw the same thing, and three copies of it had already drifted apart in size and radius.
public partial class ProjectLogoBadge : UserControl
{
    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<ProjectLogoBadge, double>(nameof(Size), 34d);

    public static readonly StyledProperty<string?> LogoPathProperty =
        AvaloniaProperty.Register<ProjectLogoBadge, string?>(nameof(LogoPath));

    // The project's name, for the initial shown when it has no logo.
    public static readonly StyledProperty<string?> ProjectNameProperty =
        AvaloniaProperty.Register<ProjectLogoBadge, string?>(nameof(ProjectName));

    // Whether to fall back to the project's initial. Off for the editor's preview, where an empty well says "no logo yet" more honestly than a letter does.
    public static readonly StyledProperty<bool> ShowsInitialProperty =
        AvaloniaProperty.Register<ProjectLogoBadge, bool>(nameof(ShowsInitial), true);

    // The well's fill, so the badge sits on whichever surface hosts it rather than carrying its own.
    public static readonly StyledProperty<IBrush?> WellBackgroundProperty =
        AvaloniaProperty.Register<ProjectLogoBadge, IBrush?>(nameof(WellBackground));

    public static readonly DirectProperty<ProjectLogoBadge, double> InitialFontSizeProperty =
        AvaloniaProperty.RegisterDirect<ProjectLogoBadge, double>(nameof(InitialFontSize), badge => badge.InitialFontSize);

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public string? LogoPath
    {
        get => GetValue(LogoPathProperty);
        set => SetValue(LogoPathProperty, value);
    }

    public string? ProjectName
    {
        get => GetValue(ProjectNameProperty);
        set => SetValue(ProjectNameProperty, value);
    }

    public bool ShowsInitial
    {
        get => GetValue(ShowsInitialProperty);
        set => SetValue(ShowsInitialProperty, value);
    }

    public IBrush? WellBackground
    {
        get => GetValue(WellBackgroundProperty);
        set => SetValue(WellBackgroundProperty, value);
    }

    // The initial scales with the well, so one badge does not need a font size set beside its size.
    public double InitialFontSize => Math.Round(Size * 0.45);

    public ProjectLogoBadge()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SizeProperty)
        {
            RaisePropertyChanged(InitialFontSizeProperty, default, InitialFontSize);
        }
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Controls;

// AC-37: the one session-header identity strip for every session kind (status dot, name, kind chip,
// activity, cwd icon, branch, usage pill), bound to the shared `SessionPanelViewModel` base so SDK and
// TTY views share one definition instead of near-identical copies (why the V1 redesign missed one).
public partial class SessionHeaderBar : UserControl
{
    // The content shown on hover of the kind chip — provider-specific, so each view supplies its own (the SDK
    // header its connected-tools card, the TTY header its render diagnostics). Kept as a slot rather than baked in
    // because the two are genuinely different content, not one string; the chip has no tooltip when this is null.
    public static readonly StyledProperty<object?> KindChipTooltipProperty =
        AvaloniaProperty.Register<SessionHeaderBar, object?>(nameof(KindChipTooltip));

    public SessionHeaderBar()
    {
        InitializeComponent();
    }

    public object? KindChipTooltip
    {
        get => GetValue(KindChipTooltipProperty);
        set => SetValue(KindChipTooltipProperty, value);
    }

    private void OnRunStatusClick(object? sender, RoutedEventArgs e)
    {
        var runIds = (DataContext as SessionPanelViewModel)?.ActiveRuns.Select(run => run.RunId).ToHashSet();
        var sources = Program.Services?.GetService<CockpitViewModel>()?.PluginSupervisedActivities;
        var source = sources?.FirstOrDefault(candidate => candidate.Label == "Test runs"
                && candidate.Snapshot().Any(activity => runIds?.Contains(activity.Id) == true))
            ?? sources?.FirstOrDefault(candidate => candidate.Snapshot().Any(activity => runIds?.Contains(activity.Id) == true));
        if (source is not null && sender is Button button)
        {
            PluginStatusBarHost.ShowPanel(source, button);
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Cockpit.App.Controls;

/// <summary>
/// The one session-header identity strip for every session kind (AC-37): status dot · name · kind chip ·
/// activity (the flexible, truncating column) · cwd icon · branch · usage pill. Bound to the shared
/// <see cref="ViewModels.SessionPanelViewModel"/> base, so the SDK (<c>SessionView</c>) and TTY (<c>TtyView</c>)
/// headers are one definition rather than two near-identical copies — the copies were why the V1 redesign first
/// landed on only one of them. Each view keeps its own provider-specific controls, docked beside this bar.
/// </summary>
public partial class SessionHeaderBar : UserControl
{
    /// <summary>
    /// The content shown on hover of the kind chip — provider-specific, so each view supplies its own (the SDK
    /// header its connected-tools card, the TTY header its render diagnostics). Kept as a slot rather than baked in
    /// because the two are genuinely different content, not one string; the chip has no tooltip when this is null.
    /// </summary>
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

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}

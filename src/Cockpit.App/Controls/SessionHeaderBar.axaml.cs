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
    public SessionHeaderBar()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}

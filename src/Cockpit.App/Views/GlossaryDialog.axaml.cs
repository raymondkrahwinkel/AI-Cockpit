using Avalonia.Controls;
using Avalonia.Interactivity;
using Cockpit.App.Controls;

namespace Cockpit.App.Views;

/// <summary>
/// The five primitives, explained without a browser (AC-512): the guide's own depth lives on the website, but a
/// fresh install that cannot reach it yet (AC-510 — no internet, a blocked store) still needs these words.
/// </summary>
public partial class GlossaryDialog : Window
{
    public GlossaryDialog()
    {
        InitializeComponent();
        CockpitWindowChrome.Apply(this);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}

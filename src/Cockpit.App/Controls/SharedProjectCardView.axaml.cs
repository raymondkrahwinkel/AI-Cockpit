using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Cockpit.App.Controls;

// A shared project this machine has not added yet, drawn the same way everywhere it appears (AC-772).
//
// The command is a property rather than something the control reaches for itself: it lives on `ProjectsViewModel`,
// and the two screens that host this control sit at different depths of nested ItemsControls, so neither `$parent`
// path would work for both. One binding at each use site is the price of the control being host-agnostic.
public partial class SharedProjectCardView : UserControl
{
    // Runs with the `SharedProject` as its parameter. Null leaves the button inert, which is what the previewer and
    // the Screenshotter want.
    public static readonly StyledProperty<ICommand?> AddCommandProperty =
        AvaloniaProperty.Register<SharedProjectCardView, ICommand?>(nameof(AddCommand));

    public ICommand? AddCommand
    {
        get => GetValue(AddCommandProperty);
        set => SetValue(AddCommandProperty, value);
    }

    public SharedProjectCardView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}

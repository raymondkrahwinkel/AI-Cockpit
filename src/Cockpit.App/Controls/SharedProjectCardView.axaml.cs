using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Cockpit.App.Controls;

// AC-772: a shared project this machine has not added yet, drawn the same way everywhere it appears. The command is
// a property because its two hosts nest it at different depths, so no single `$parent` path reaches both.
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

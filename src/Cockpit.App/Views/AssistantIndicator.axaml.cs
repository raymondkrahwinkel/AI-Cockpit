using Avalonia;
using Avalonia.Controls;

namespace Cockpit.App.Views;

// AC-543: plain UserControl over AssistantIndicatorViewModel, deliberately without a
// constructor arg or host reference — whatever embeds it (sidebar, AC-238 companion window)
// supplies its own view model instance, so this file never has to know which one it is in.
public partial class AssistantIndicator : UserControl
{
    public static readonly StyledProperty<bool> ShowWhenDisabledProperty =
        AvaloniaProperty.Register<AssistantIndicator, bool>(nameof(ShowWhenDisabled));

    public bool ShowWhenDisabled
    {
        get => GetValue(ShowWhenDisabledProperty);
        set => SetValue(ShowWhenDisabledProperty, value);
    }

    public AssistantIndicator()
    {
        InitializeComponent();
    }
}

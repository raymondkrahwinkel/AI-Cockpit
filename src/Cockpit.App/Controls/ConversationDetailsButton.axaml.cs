using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Controls;

// AC-1533: ⋯ → Details over a Simple conversation — a session's header or the assistant's.
public partial class ConversationDetailsButton : UserControl
{
    public static readonly StyledProperty<string?> ProjectNameProperty =
        AvaloniaProperty.Register<ConversationDetailsButton, string?>(nameof(ProjectName));

    public string? ProjectName
    {
        get => GetValue(ProjectNameProperty);
        set => SetValue(ProjectNameProperty, value);
    }

    public ConversationDetailsButton()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void _OnOpening(object? sender, EventArgs e) =>
        this.FindControl<StackPanel>("DetailsPanel")!.DataContext = DataContext switch
        {
            AssistantChatViewModel assistant => assistant.Details(),
            SessionPanelViewModel session => ConversationDetails.For(session, ProjectName),
            _ => null,
        };

    private void _OnAction(object? sender, RoutedEventArgs e) => this.FindControl<Button>("DetailsButton")!.Flyout?.Hide();
}

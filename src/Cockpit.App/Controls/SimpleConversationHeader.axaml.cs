using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Cockpit.App.Controls;

// AC-1529: the Simple stand's header over a conversation — project › title, the tree's labels, the stand in plain
// words and Stop. One control over both routes: it reads only what start and statusline give (AC-294).
public partial class SimpleConversationHeader : UserControl
{
    // Resolved by the host from the session's `ProjectId`: a session carries the id, the cockpit holds the names.
    public static readonly StyledProperty<string?> ProjectNameProperty =
        AvaloniaProperty.Register<SimpleConversationHeader, string?>(nameof(ProjectName));

    public string? ProjectName
    {
        get => GetValue(ProjectNameProperty);
        set => SetValue(ProjectNameProperty, value);
    }

    // AC-1532: opens Earlier conversations, which the cockpit owns and a session does not.
    public static readonly StyledProperty<ICommand?> EarlierCommandProperty =
        AvaloniaProperty.Register<SimpleConversationHeader, ICommand?>(nameof(EarlierCommand));

    public ICommand? EarlierCommand
    {
        get => GetValue(EarlierCommandProperty);
        set => SetValue(EarlierCommandProperty, value);
    }

    public SimpleConversationHeader()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // AC-1532: below this width Earlier conversations keeps only its icon, so the title and the stand keep their room.
    private const double EarlierLabelMinWidth = 760;

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        this.FindControl<TextBlock>("SimpleEarlierLabel")!.IsVisible = e.NewSize.Width >= EarlierLabelMinWidth;
    }

    private void _OnRenameKeyDown(object? sender, KeyEventArgs e) => InlineRename.OnKeyDown(sender, e);

    private void _OnRenameLostFocus(object? sender, RoutedEventArgs e) => InlineRename.OnLostFocus(sender, e);

    private void _OnRenameAttached(object? sender, VisualTreeAttachmentEventArgs e) => InlineRename.OnAttached(sender, e);
}

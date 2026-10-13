using Avalonia;
using Avalonia.Controls;
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

    public SimpleConversationHeader()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}

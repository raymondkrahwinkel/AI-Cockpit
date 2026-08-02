using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Cockpit.App.Services;
using Cockpit.Core.Projects;

namespace Cockpit.App.Controls;

// A project's extra information (AC-295) in the shape every surface shows it in: a label over its value, and a
// value that is a web address drawn as a link. One control, shared by the overview's cards and the manager's rows,
// so the two cannot drift apart the way the logo well did before `ProjectLogoBadge`.
public partial class ProjectInfoList : UserControl
{
    // The rows to show — a project's `Project.AdditionalInfo`. Empty or null draws nothing.
    public static readonly StyledProperty<IEnumerable?> FieldsProperty =
        AvaloniaProperty.Register<ProjectInfoList, IEnumerable?>(nameof(Fields));

    public IEnumerable? Fields
    {
        get => GetValue(FieldsProperty);
        set => SetValue(FieldsProperty, value);
    }

    public ProjectInfoList()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnOpenLink(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ProjectInfoField field })
        {
            // The row only draws a button for a value that is already an http(s) URL, so a refusal here means the
            // browser would not start — nothing this control can do about that, and nothing worth a dialog.
            _ = ExternalLink.TryOpen(field.Value);
        }
    }

    // Stops a second click on a link from reaching whatever hosts these rows. The projects window puts them inside a
    // card that opens the project editor when it is double-clicked; a button swallows the pointer press, but the
    // double-tap gesture bubbles past it, so clicking a link twice opened the browser and the editor behind it.
    private void OnLinkDoubleTapped(object? sender, TappedEventArgs e) => e.Handled = true;
}

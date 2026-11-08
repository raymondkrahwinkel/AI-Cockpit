using System.ComponentModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Controls;

// The @-mention list shared by every chat window. It hangs on the caret line of Input, as wide as Frame, and opens to
// the side of that line with the most room inside the window, never over the title bar.
public partial class MentionPopup : UserControl
{
    public const double MaxListHeight = 340;

    private const double EdgeMargin = 8;
    private const double MinListHeight = 96;
    private const double NarrowWidth = 360;

    public static readonly StyledProperty<TextBox?> InputProperty =
        AvaloniaProperty.Register<MentionPopup, TextBox?>(nameof(Input));

    public static readonly StyledProperty<Control?> FrameProperty =
        AvaloniaProperty.Register<MentionPopup, Control?>(nameof(Frame));

    public static readonly StyledProperty<string?> EmptyActionTextProperty =
        AvaloniaProperty.Register<MentionPopup, string?>(nameof(EmptyActionText));

    public static readonly StyledProperty<ICommand?> EmptyActionCommandProperty =
        AvaloniaProperty.Register<MentionPopup, ICommand?>(nameof(EmptyActionCommand));

    public static readonly StyledProperty<string?> StickyHeadingProperty =
        AvaloniaProperty.Register<MentionPopup, string?>(nameof(StickyHeading));

    private MentionPickerViewModel? _picker;

    public MentionPopup()
    {
        InitializeComponent();
        this.FindControl<ListBox>("List")!.AddHandler(ScrollViewer.ScrollChangedEvent, (_, _) => _UpdateStickyHeading());
    }

    // The composer's text box, whose text layout gives the caret line.
    public TextBox? Input
    {
        get => GetValue(InputProperty);
        set => SetValue(InputProperty, value);
    }

    // The composer's frame: the list takes its width and its left edge.
    public Control? Frame
    {
        get => GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    // The empty state's way out ("Choose folder…" on Home); it arrives as a callback so this control knows nothing of Home.
    public string? EmptyActionText
    {
        get => GetValue(EmptyActionTextProperty);
        set => SetValue(EmptyActionTextProperty, value);
    }

    public ICommand? EmptyActionCommand
    {
        get => GetValue(EmptyActionCommandProperty);
        set => SetValue(EmptyActionCommandProperty, value);
    }

    public string? StickyHeading
    {
        get => GetValue(StickyHeadingProperty);
        private set => SetValue(StickyHeadingProperty, value);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_picker is not null)
        {
            _picker.PropertyChanged -= _OnPickerChanged;
        }

        _picker = DataContext as MentionPickerViewModel;
        if (_picker is not null)
        {
            _picker.PropertyChanged += _OnPickerChanged;
        }

        _Sync();
    }

    private void _OnPickerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MentionPickerViewModel.IsOpen))
        {
            _Sync();
        }
    }

    private void _Sync()
    {
        var host = this.FindControl<Popup>("Host")!;
        if (_picker is not { IsOpen: true } picker)
        {
            host.IsOpen = false;
            StickyHeading = null;
            return;
        }

        _Place(host, picker.TokenStart);
        host.IsOpen = true;
    }

    // The caret line is the line holding the '@'. Below: the list starts at the bottom of that line. Above: it ends at the top of it.
    private void _Place(Popup host, int tokenStart)
    {
        var card = this.FindControl<Border>("Card")!;
        if (Frame is not { } frame || TopLevel.GetTopLevel(frame) is not { } top)
        {
            return;
        }

        var line = new Rect(0, 0, frame.Bounds.Width, frame.Bounds.Height);
        if (Input?.FindDescendantOfType<TextPresenter>() is { } presenter
            && presenter.TextLayout is { } layout
            && presenter.TranslatePoint(default, frame) is { } origin)
        {
            var caret = layout.HitTestTextPosition(Math.Max(0, tokenStart));
            line = new Rect(0, origin.Y + caret.Top, frame.Bounds.Width, Math.Max(caret.Height, 1));
        }

        var lineTop = frame.TranslatePoint(line.TopLeft, top)?.Y ?? 0;
        var above = lineTop - _ContentTop(top, frame) - EdgeMargin;
        var below = top.ClientSize.Height - (lineTop + line.Height) - EdgeMargin;
        var down = below >= above;

        host.PlacementTarget = frame;
        host.PlacementRect = line;
        host.Placement = down ? PlacementMode.Bottom : PlacementMode.Top;
        card.Width = frame.Bounds.Width;
        card.Classes.Set("narrow", frame.Bounds.Width < NarrowWidth);
        card.MaxHeight = Math.Clamp(down ? below : above, MinListHeight, MaxListHeight);
    }

    // Where the window content starts: below the title bar the window chrome docks at the top, when there is one.
    private static double _ContentTop(TopLevel top, Visual frame)
    {
        var root = (top as ContentControl)?.Content as Control;
        if (root is Border { Child: { } inner })
        {
            root = inner;
        }

        if (root is DockPanel { Children.Count: > 0 } dock
            && dock.Children[0] is { } bar
            && DockPanel.GetDock(bar) == Dock.Top
            && !bar.IsVisualAncestorOf(frame))
        {
            return bar.TranslatePoint(new Point(0, bar.Bounds.Height), top)?.Y ?? 0;
        }

        return 0;
    }

    // The group heading at or above the first row in view, shown once the list has scrolled away from its first heading.
    private void _UpdateStickyHeading()
    {
        var list = this.FindControl<ListBox>("List")!;
        if (_picker is null || list.Scroll is not { Offset.Y: > 0 })
        {
            StickyHeading = null;
            return;
        }

        var first = list.GetRealizedContainers()
            .Select(container => (Index: list.IndexFromContainer(container), Top: container.TranslatePoint(default, list)?.Y ?? 0, container.Bounds.Height))
            .Where(row => row.Index >= 0 && row.Top + row.Height > 0)
            .OrderBy(row => row.Index)
            .FirstOrDefault();
        var heading = first.Height > 0
            ? _picker.Matches.Take(first.Index + 1).LastOrDefault(match => match.IsHeading)
            : null;
        StickyHeading = heading?.Heading;
    }
}

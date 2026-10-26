using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Material.Icons;
using Material.Icons.Avalonia;

namespace Cockpit.App.Controls;

// AC-1762: the mockups' foldable `.sec` card (Company v8). Projects, This laptop and every server stand in one, so the
// three group headings look the same; the whole header folds it, and where that is remembered is the caller's business.
internal sealed class SidebarSection : ContentControl
{
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<SidebarSection, string?>(nameof(Title));

    // The `n` in `Title · n`; empty hides the `·`.
    public static readonly StyledProperty<string?> SubtitleProperty = AvaloniaProperty.Register<SidebarSection, string?>(nameof(Subtitle));

    // Text (a latency) or a control (buttons) at the right of the header; pressing a button there does not fold the card.
    public static readonly StyledProperty<object?> TrailingProperty = AvaloniaProperty.Register<SidebarSection, object?>(nameof(Trailing));

    // A small faint icon before the title (the server's), or none.
    public static readonly StyledProperty<MaterialIconKind?> IconProperty = AvaloniaProperty.Register<SidebarSection, MaterialIconKind?>(nameof(Icon));

    public static readonly StyledProperty<bool> IsExpandedProperty = AvaloniaProperty.Register<SidebarSection, bool>(
        nameof(IsExpanded), defaultValue: true, defaultBindingMode: BindingMode.TwoWay);

    public SidebarSection()
    {
        Template = new FuncControlTemplate<SidebarSection>(_Build);
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Subtitle
    {
        get => GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public object? Trailing
    {
        get => GetValue(TrailingProperty);
        set => SetValue(TrailingProperty, value);
    }

    public MaterialIconKind? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public bool IsExpanded
    {
        get => GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    private static Control _Build(SidebarSection owner, INameScope scope)
    {
        var chevron = CockpitIcons.Icon(MaterialIconKind.ChevronDown, 12);
        chevron.VerticalAlignment = VerticalAlignment.Center;
        chevron.Bind(TemplatedControl.ForegroundProperty, owner.GetResourceObservable("CockpitTextFaintBrush"));
        chevron.Bind(MaterialIcon.KindProperty, owner.GetObservable(IsExpandedProperty,
            expanded => expanded ? MaterialIconKind.ChevronDown : MaterialIconKind.ChevronRight).ToBinding());

        var icon = CockpitIcons.Icon(MaterialIconKind.Server, 13);
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Bind(TemplatedControl.ForegroundProperty, owner.GetResourceObservable("CockpitTextFaintBrush"));
        icon.Bind(MaterialIcon.KindProperty, owner.GetObservable(IconProperty, kind => kind ?? MaterialIconKind.Server).ToBinding());
        icon.Bind(Visual.IsVisibleProperty, owner.GetObservable(IconProperty, kind => kind is not null));

        var title = new TextBlock
        {
            FontSize = 12.5,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        title.Bind(TextBlock.TextProperty, owner.GetObservable(TitleProperty));
        title.Bind(TextBlock.ForegroundProperty, owner.GetResourceObservable("CockpitTextPrimaryBrush"));

        var count = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        count.Bind(TextBlock.TextProperty, owner.GetObservable(SubtitleProperty, text => $"· {text}"));
        count.Bind(Visual.IsVisibleProperty, owner.GetObservable(SubtitleProperty, text => !string.IsNullOrEmpty(text)));
        count.Bind(TextBlock.ForegroundProperty, owner.GetResourceObservable("CockpitTextFaintBrush"));

        var trailing = new ContentPresenter
        {
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        trailing.Bind(ContentPresenter.ContentProperty, owner.GetObservable(TrailingProperty));
        trailing.Bind(ContentPresenter.ForegroundProperty, owner.GetResourceObservable("CockpitTextFaintBrush"));

        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto,*,Auto"),
            ColumnSpacing = 7,
            Children = { chevron, icon, title, count, trailing },
        };
        Grid.SetColumn(icon, 1);
        Grid.SetColumn(title, 2);
        Grid.SetColumn(count, 3);
        Grid.SetColumn(trailing, 5);

        var header = new Border
        {
            Padding = new Thickness(9, 7),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = headerGrid,
        };
        header.PointerPressed += (_, e) =>
        {
            // A button in the header owns its own press; only the rest of the row folds the card.
            if (e.Source is Visual source && (source as Button ?? source.FindAncestorOfType<Button>()) is not null)
            {
                return;
            }

            owner.IsExpanded = !owner.IsExpanded;
        };

        var body = new ContentPresenter { Margin = new Thickness(4, 2, 4, 6) };
        body.Bind(ContentPresenter.ContentProperty, owner.GetObservable(ContentProperty));
        body.Bind(ContentPresenter.ContentTemplateProperty, owner.GetObservable(ContentTemplateProperty));
        body.Bind(Visual.IsVisibleProperty, owner.GetObservable(IsExpandedProperty));

        var card = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Child = new StackPanel { Children = { header, body } },
        };
        card.Bind(Border.BackgroundProperty, owner.GetResourceObservable("CockpitSecondaryBgBrush"));
        card.Bind(Border.BorderBrushProperty, owner.GetResourceObservable("CockpitHairlineBrush"));
        return card;
    }
}

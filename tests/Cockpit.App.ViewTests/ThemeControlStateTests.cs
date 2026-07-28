using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The control states AC-336 took over from the Fluent theme. Each of these is a colour a control resolves at
/// runtime out of a template, which is exactly the kind of thing reading the markup cannot tell you: the theme sets
/// a property, the template may or may not use it, and a state nobody claimed quietly keeps Fluent's own palette.
/// Two of these shipped wrong for months for that reason — a selected row went light grey the moment it was
/// clicked, and a disabled dropdown came out lighter than the editable fields around it.
/// </summary>
[Collection("avalonia")]
public class ThemeControlStateTests
{
    [Fact]
    public void ATickedCheckBox_DrawsInTheCockpitAccent_NotTheSystemOne() => HeadlessAvalonia.Run(() =>
    {
        // Avalonia's own accent (#0078d7) is what the box used before this. It looks close enough to the theme's
        // blue to pass an eyeball test, and would have stayed behind the day the accent token moves.
        var box = new CheckBox { Content = "x", IsChecked = true };
        using var host = _Shown(box);

        var fill = _Fill(box, "NormalRectangle");

        // The tick follows the theme's accent, not the platform's.
        Assert.Equal(_Token("CockpitAccentColor"), fill);
    });

    [Fact]
    public void APickedRadioButton_DrawsInTheCockpitAccent_NotTheSystemOne() => HeadlessAvalonia.Run(() =>
    {
        // The same defect as the CheckBox above, one control further along: the ring was filled and stroked with
        // Avalonia's #0078d7 while the theme's accent is #3b82f6 (AC-404, found by the AC-338 palette baseline).
        var choice = new RadioButton { Content = "x", IsChecked = true };
        using var host = _Shown(choice);

        Assert.Equal(_Token("CockpitAccentColor"), _Stroke(choice, "Ring"));
        Assert.Equal(_Token("CockpitAccentColor"), _EllipseFill(choice, "Ring"));
        // The dot has to read on that fill, so it is the on-accent ink rather than the ring's own.
        Assert.Equal(_Token("CockpitTextOnAccentColor"), _EllipseFill(choice, "Dot"));
        Assert.True(_Part<Ellipse>(choice, "Dot").IsVisible, "a picked radio button shows its dot");
    });

    [Fact]
    public void APickedRadioButton_ActuallyPaintsItsRing() => HeadlessAvalonia.Run(() =>
    {
        // The three assertions above were all true of a button that came out as a white dot on nothing: Fluent's
        // own radio rules are still loaded, they match template parts by name, and one of them fades a part called
        // OuterEllipse to Opacity 0 on :checked. A brush is what a control was told to paint with; this is whether
        // any of it reached the screen.
        var choice = new RadioButton { Content = "x", IsChecked = true };
        using var host = _Shown(choice);

        var ring = _Part<Ellipse>(choice, "Ring");
        var origin = ring.TranslatePoint(new Point(2, ring.Bounds.Height / 2), host.Window) ?? default;

        // Two pixels in from the ring's left edge, which is its fill and never the dot (8px, centred in 18).
        Assert.Equal(_Channels(_Token("CockpitAccentColor")), _Channels(_PaintedAt(host.Window, origin)));
    });

    [Fact]
    public void AnUnpickedRadioButton_CarriesTheThemesOwnSurfaceAndHairline() => HeadlessAvalonia.Run(() =>
    {
        // Asserted against the tokens rather than against the unticked CheckBox: that one renders its box
        // Transparent, so the theme's CockpitPanelBgBrush setter does not reach its unchecked state. Pinning the
        // radio to what the CheckBox happens to render would pin it to that, which is not what the theme says.
        var choice = new RadioButton { Content = "x", IsChecked = false };
        using var host = _Shown(choice);

        Assert.Equal(_Token("CockpitPanelBgColor"), _EllipseFill(choice, "Ring"));
        Assert.Equal(_Token("CockpitHairlineColor"), _Stroke(choice, "Ring"));
        Assert.False(_Part<Ellipse>(choice, "Dot").IsVisible, "an unpicked radio button shows no dot");
    });

    [Fact]
    public void ADisabledRadioButton_RecedesOnTheSameTokensAsADisabledCheckBox() => HeadlessAvalonia.Run(() =>
    {
        var choice = new RadioButton { Content = "x", IsChecked = false, IsEnabled = false };
        using var host = _Shown(choice);

        Assert.Equal(_Token("CockpitSecondaryBgColor"), _EllipseFill(choice, "Ring"));
        Assert.Equal(_Token("CockpitHairlineSoftColor"), _Stroke(choice, "Ring"));
    });

    [Fact]
    public void AScrollBar_DrawsInTheThemeRatherThanFluentsOwnGreys() => HeadlessAvalonia.Run(() =>
    {
        // The theme never claimed a ScrollBar, so every scrollable surface carried Fluent's #1F1F1F track and
        // corner and its #858585 thumb — colours no source lint could find, because we never wrote them (AC-405).
        var viewer = _Scrolled();
        using var host = _Shown(viewer);

        Assert.Equal(_Token("CockpitInsetBgColor"), _ColourOf(_Named<Rectangle>(viewer, "TrackRect").Fill));
        Assert.Equal(_Token("CockpitInsetBgColor"), _ColourOf(_Named<Panel>(viewer, "PART_ScrollBarsSeparator").Background));
        Assert.Equal(_Token("CockpitTextFaintColor"), _ColourOf(viewer.GetVisualDescendants().OfType<Thumb>().First().Background));
    });

    [Fact]
    public void AScrollBarThumb_StandsOutFromTheTrackItSlidesOn() => HeadlessAvalonia.Run(() =>
    {
        // The obvious way to get this wrong: every colour resolves to a theme token and the thumb is then
        // invisible in its own groove. No assertion about tokens can see that; this one is about the gap.
        var viewer = _Scrolled();
        using var host = _Shown(viewer);

        var thumb = _Brightness(_ColourOf(viewer.GetVisualDescendants().OfType<Thumb>().First().Background));
        var track = _Brightness(_ColourOf(_Named<Rectangle>(viewer, "TrackRect").Fill));

        Assert.True(thumb - track > 30, $"a thumb at {thumb:F0} on a track at {track:F0} is not a thumb anyone can see");
    });

    [Fact]
    public void AScrollBarThumb_ActuallyPaintsThatColour() => HeadlessAvalonia.Run(() =>
    {
        // Same reason as the radio button's ring: a brush is what a control was told to paint with, and the
        // question here is what reached the screen.
        var viewer = _Scrolled();
        using var host = _Shown(viewer);

        var thumb = viewer.GetVisualDescendants().OfType<Thumb>().First();
        var middle = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), host.Window) ?? default;

        Assert.Equal(_Channels(_Token("CockpitTextFaintColor")), _Channels(_PaintedAt(host.Window, middle)));
    });

    [Fact]
    public void ADisabledComboBox_RecedesRatherThanStandingOut() => HeadlessAvalonia.Run(() =>
    {
        // Left to Fluent, a disabled picker is drawn *lighter* than its enabled neighbours: it steps forward on a
        // dark form, which is the opposite of what disabled means ("Provider (fixed after creation)").
        var enabled = new ComboBox { ItemsSource = new[] { "a" }, SelectedIndex = 0 };
        var disabled = new ComboBox { ItemsSource = new[] { "a" }, SelectedIndex = 0, IsEnabled = false };
        using var host = _Shown(new StackPanel { Children = { enabled, disabled } });

        var lit = _Fill(enabled, "Background");
        var dimmed = _Fill(disabled, "Background");

        // A control you cannot use sits behind the ones you can, never in front of them.
        Assert.True(_Brightness(dimmed) < _Brightness(lit), $"disabled {dimmed} is not darker than enabled {lit}");
    });

    [Fact]
    public void ASelectedRow_KeepsTheThemeFill_OnceItHasBeenClicked() => HeadlessAvalonia.Run(() =>
    {
        // AC-336 reported this as a bug: a selected row going light grey with dark text the moment it was clicked
        // (Discover in the plugin store, the picked profile in ManageProfiles), because :selected was styled and
        // :selected:focus was not. It could not be reproduced here — with the focus rules taken out the fill stays
        // correct in this harness, so what this test pins down is the state, not a repair. Kept because the theme
        // now claims :selected:focus explicitly instead of relying on Fluent's rule order to lose.
        var list = new ListBox { ItemsSource = new[] { "a", "b" }, SelectedIndex = 0 };
        using var host = _Shown(list);

        var row = list.GetVisualDescendants().OfType<ListBoxItem>().First();
        row.Focus();
        host.Window.UpdateLayout();

        var fill = _PresenterFill(row);

        Assert.True(row.IsFocused, "the test is only meaningful while the row actually holds focus");
        // A clicked row looks the same as a selected one.
        Assert.Equal(_Token("CockpitPanelBgColor"), fill);
    });

    [Fact]
    public void ADisabledButton_KeepsTheThemeSurface() => HeadlessAvalonia.Run(() =>
    {
        var button = new Button { Content = "x", IsEnabled = false };
        using var host = _Shown(button);

        var fill = _PresenterFill(button);

        // A disabled button keeps its shape and fades its label rather than becoming Fluent's grey slab.
        Assert.Equal(_Token("CockpitPanelBgColor"), fill);
    });

    [Theory]
    [InlineData("Button")]
    [InlineData("Accent")]
    [InlineData("Ghost")]
    [InlineData("Subtle")]
    [InlineData("RowAction")]
    public void ADisabledButton_FadesItsLabel(string variant) => HeadlessAvalonia.Run(() =>
    {
        // The label is the part that says "not now"; the fill barely moves. Three rules in this theme tried to
        // fade it by setting TextBlock.Foreground on the presenter and none of them ever did anything: the
        // theme's own `:is(TextBlock)` rule names the text element directly, and a named rule beats a value
        // inherited from an ancestor. Rendering is what showed it — every one of these looked enabled.
        var button = new Button { Content = "x", IsEnabled = false };
        if (variant != "Button")
        {
            button.Classes.Add(variant);
        }

        using var host = _Shown(button);

        var label = button.GetVisualDescendants().OfType<TextBlock>().First();

        // A disabled button's label has to read as unavailable, whichever variant it is.
        Assert.Equal(_Token("CockpitTextFaintColor"), _ColourOf(label.Foreground));
    });

    [Fact]
    public void ADisabledPicker_FadesItsChosenValue() => HeadlessAvalonia.Run(() =>
    {
        var picker = new ComboBox { ItemsSource = new[] { "Claude CLI" }, SelectedIndex = 0, IsEnabled = false };
        using var host = _Shown(picker);

        var value = picker.GetVisualDescendants().OfType<TextBlock>()
            .First(block => block.Text == "Claude CLI");

        // The value recedes with the surface it sits on, rather than staying bright on a dimmed field.
        Assert.Equal(_Token("CockpitTextFaintColor"), _ColourOf(value.Foreground));
    });

    [Fact]
    public void ASwitch_MovesItsKnobAcrossTheTrack() => HeadlessAvalonia.Run(() =>
    {
        var off = new CheckBox { Classes = { "Switch" }, Content = "x", IsChecked = false };
        var on = new CheckBox { Classes = { "Switch" }, Content = "x", IsChecked = true };
        using var host = _Shown(new StackPanel { Children = { off, on } });

        var travelled = _KnobOffset(on) - _KnobOffset(off);
        var trackWidth = _Part<Border>(on, "Track").Bounds.Width;

        Assert.True(travelled > 0, $"a switch that is on shows its knob at the other end, but it moved {travelled}px");
        Assert.True(travelled < trackWidth, $"the knob travels within the {trackWidth}px track, not {travelled}px");
        // An on switch carries the accent.
        Assert.Equal(_Token("CockpitAccentColor"), _Fill(on, "Track"));
    });

    [Fact]
    public void TextInAPopup_IsStillTheThemesTextColour() => HeadlessAvalonia.Run(() =>
    {
        // The base text colour is inherited from the top level rather than stamped onto each text block, which is
        // what lets a control tint its own label. A popup is its own top level — an open dropdown, a tooltip and a
        // flyout do not hang under the window — so anything inherited from the window alone would stop at its edge
        // and the whole open list would fall back to Fluent's colour.
        var picker = new ComboBox { ItemsSource = new[] { "Claude · Opus", "Claude · Sonnet" }, SelectedIndex = 0 };
        using var host = _Shown(picker);

        picker.IsDropDownOpen = true;
        host.Window.UpdateLayout();

        // Through the container, not the visual tree: the popup is a separate root, so the open list is not
        // among the picker's visual descendants — which is the whole point of this test.
        var item = picker.ContainerFromIndex(0);
        Assert.NotNull(item);

        var itemText = item!.GetVisualDescendants().OfType<TextBlock>().First();

        // An open list is drawn on its own surface, and has to be readable there too.
        Assert.Equal(_Token("CockpitTextPrimaryColor"), _ColourOf(itemText.Foreground));
    });

    [Fact]
    public void AnInput_SitsAboveTheWindowRatherThanSunkIntoIt() => HeadlessAvalonia.Run(() =>
    {
        // The mockup puts inputs a step lighter than the window they are on, so a form reads as a column of things
        // you can type in. They used to be darker, which reads as a recess.
        var box = new TextBox { Text = "x" };
        using var host = _Shown(box);

        var fill = _Fill(box, "PART_BorderElement");

        var window = _Token("CockpitWindowBgColor");
        Assert.True(_Brightness(fill) > _Brightness(window),
            $"an input is raised off the window, but {fill} is not lighter than {window}");
    });

    [Fact]
    public void AnInputAndAPicker_ShareTheSameBox() => HeadlessAvalonia.Run(() =>
    {
        // A label/field grid only lines up if the typed row and the picked row are the same height and shape.
        var box = new TextBox { Text = "x" };
        var picker = new ComboBox { ItemsSource = new[] { "a" }, SelectedIndex = 0 };
        using var host = _Shown(new StackPanel { Children = { box, picker } });

        Assert.Equal(_Fill(picker, "Background"), _Fill(box, "PART_BorderElement"));
        Assert.Equal(picker.CornerRadius, box.CornerRadius);
        // Same inset, so their text sits on the same line.
        Assert.Equal(picker.Padding, box.Padding);
    });

    /// <summary>The colour a brush paints, and a readable failure when it is not a plain colour at all.</summary>
    private static Color _ColourOf(IBrush? brush) =>
        brush is ISolidColorBrush solid
            ? solid.Color
            : throw new InvalidOperationException($"expected a plain colour, got {brush?.ToString() ?? "nothing"}");

    /// <summary>A scroll viewer whose content overflows both ways, so both bars and the corner between them exist.</summary>
    private static ScrollViewer _Scrolled()
    {
        var content = new StackPanel();
        for (var line = 0; line < 40; line++)
        {
            content.Children.Add(new TextBlock { Text = new string('x', 200) });
        }

        return new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Visible,
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
        };
    }

    /// <summary>A named element anywhere below the control — a scroll bar's parts sit in a template of their own.</summary>
    private static T _Named<T>(Control control, string name) where T : StyledElement =>
        control.GetVisualDescendants().OfType<T>().First(part => part.Name == name);

    /// <summary>The colour actually rendered at a point of the window, read back out of the frame.</summary>
    private static Color _PaintedAt(Window window, Point point)
    {
        using var frame = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("the headless renderer produced no frame to sample");
        using var buffer = frame.Lock();

        var bytesPerPixel = buffer.RowBytes / buffer.Size.Width;
        var row = new byte[buffer.RowBytes];
        Marshal.Copy(buffer.Address + ((int)point.Y * buffer.RowBytes), row, 0, row.Length);

        var offset = (int)point.X * bytesPerPixel;
        return Color.FromRgb(row[offset], row[offset + 1], row[offset + 2]);
    }

    /// <summary>
    /// A colour's three channels, sorted. The frame's channel order is the platform's business (BGRA on one, RGBA
    /// on another), and this comparison is about which colour was painted, not about how the buffer stores it.
    /// </summary>
    private static IReadOnlyList<byte> _Channels(Color colour) => [.. new[] { colour.R, colour.G, colour.B }.Order()];

    private static Color _Fill(Control control, string part) =>
        ((ISolidColorBrush)_Part<Border>(control, part).Background!).Color;

    private static Color _EllipseFill(Control control, string part) =>
        _ColourOf(_Part<Ellipse>(control, part).Fill);

    private static Color _Stroke(Control control, string part) =>
        _ColourOf(_Part<Ellipse>(control, part).Stroke);

    private static Color _PresenterFill(Control control) =>
        ((ISolidColorBrush)control.GetVisualDescendants()
            .OfType<ContentPresenter>()
            .Select(presenter => presenter.Background)
            .First(background => background is ISolidColorBrush)!).Color;

    private static double _KnobOffset(Control control)
    {
        var knob = _Part<Ellipse>(control, "Knob");
        return (knob.TranslatePoint(default, _Part<Border>(control, "Track")) ?? default).X;
    }

    private static T _Part<T>(Control control, string name) where T : Control =>
        control.GetVisualDescendants().OfType<T>().First(part => part.Name == name);

    private static Color _Token(string key) =>
        (Color)(Application.Current?.FindResource(key) ?? throw new InvalidOperationException($"no token '{key}'"));

    /// <summary>Perceived lightness — enough to say which of two surfaces sits in front of the other.</summary>
    private static double _Brightness(Color colour) =>
        (0.299 * colour.R) + (0.587 * colour.G) + (0.114 * colour.B);

    private static Host _Shown(Control content)
    {
        var window = new Window { Width = 400, Height = 300, Content = content };
        window.Show();
        window.UpdateLayout();

        return new Host(window);
    }

    private sealed record Host(Window Window) : IDisposable
    {
        public void Dispose() => Window.Close();
    }
}

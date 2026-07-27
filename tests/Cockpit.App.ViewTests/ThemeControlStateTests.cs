using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
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

    private static Color _Fill(Control control, string part) =>
        ((ISolidColorBrush)_Part<Border>(control, part).Background!).Color;

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

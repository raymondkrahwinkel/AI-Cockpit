using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.VisualTree;
using Cockpit.Plugin.Workflows.Engine;
using Cockpit.Plugin.Workflows.Model;
using Cockpit.Plugins.Abstractions;
using NSubstitute;

namespace Cockpit.Plugin.Workflows.Tests;

/// <summary>
/// The flow name field asks for no fill and a transparent border. The Prompt Library's
/// quick-pick search field and the command palette's query box get away with no border at all instead — each is
/// the only input on its surface and grabs focus automatically on open (<c>PromptQuickPickControl.cs</c>,
/// <c>CommandPaletteDialog.axaml.cs</c>), so there is no un-focused state left to tell a focused one apart from.
/// This field sits in a toolbar next to Back and the Active toggle and is never auto-focused, so hover and focus
/// need a border of their own to draw on. Measured against a rendered window, not asserted from reading the
/// setters — a claimed state only proves itself in a real render (AC-336).
/// </summary>
[Collection("avalonia")]
public class RenameFieldThemeStateTests
{
    [Fact]
    public void RestHoverAndFocus_EachDrawTheTokenTheThemeClaimsForThatState()
    {
        using var host = _Shown(_EditorControl());
        var window = host.Window;

        var field = window.GetVisualDescendants().OfType<TextBox>()
            .First(box => Equals(ToolTip.GetTip(box), "The flow's name — type to change it"));
        var border = field.GetVisualDescendants().OfType<Border>().First(part => part.Name == "PART_BorderElement");
        var thickness = border.BorderThickness;

        // Rest: the field's own transparent brush comes through the template binding, so there is nothing to see.
        Assert.Equal(Colors.Transparent, _Colour(border.BorderBrush));
        // Invisible rather than absent — a border of zero leaves the states below nothing to draw. Held to the
        // theme's own width rather than to a number, so the field keeps the height of every other input in the
        // toolbar: if that token moves, this field is meant to move with it.
        Assert.Equal(_ThicknessToken("CockpitHairlineThickness"), thickness);

        window.MouseMove(field.TranslatePoint(new Point(5, 5), window) ?? default);
        window.UpdateLayout();
        Assert.True(field.IsPointerOver, "the hover assertion only means something while the pointer is on the field");
        Assert.Equal(_Token("CockpitHairlineHoverColor"), _Colour(border.BorderBrush));

        window.MouseMove(new Point(window.Width - 1, window.Height - 1));
        field.Focus();
        window.UpdateLayout();
        Assert.False(field.IsPointerOver, "focus has to be read on its own, or hover answers for it");
        Assert.True(field.IsFocused, "and only while the field actually holds focus");
        // The focus token, not the accent: the host theme gives a plain :focus CockpitFocusHairlineBrush on purpose
        // (Theme.axaml, the TextBox :focus setter and the comment above it) — the accent ring is reserved for
        // :focus-visible, so that a field reached by mouse is marked more quietly than one reached by keyboard.
        // Asserting the accent here made this test disagree with the theme it claims to read; it only passed while
        // the two tokens happened to share a hue, and it started failing the moment the accent was darkened for
        // contrast (AC-381) — for a reason that has nothing to do with this field.
        Assert.Equal(_Token("CockpitFocusHairlineColor"), _Colour(border.BorderBrush));

        // The two things the states must not touch: the fill it reads as a label by, and the width that would
        // shift the toolbar around it.
        Assert.Equal(Colors.Transparent, _Colour(border.Background));
        Assert.Equal(thickness, border.BorderThickness);
    }

    /// <summary>The control in a shown, laid-out window, closed again even when an assertion below it fails.</summary>
    private static Host _Shown(Control content)
    {
        var window = new Window { Width = 500, Height = 300, Content = content };
        window.Show();
        window.UpdateLayout();

        return new Host(window);
    }

    private sealed record Host(Window Window) : IDisposable
    {
        public void Dispose() => Window.Close();
    }

    private static Thickness _ThicknessToken(string key) =>
        (Thickness)(Application.Current?.FindResource(key) ?? throw new InvalidOperationException($"no token '{key}'"));

    private static Color _Colour(IBrush? brush) =>
        brush is ISolidColorBrush solid
            ? solid.Color
            : throw new InvalidOperationException($"expected a plain colour, got {brush?.ToString() ?? "nothing"}");

    private static WorkflowEditorControl _EditorControl()
    {
        var workflow = new Workflow { Id = "w1", Name = "My Flow" };
        var host = Substitute.For<ICockpitHost>();
        host.WorkflowSteps.Returns([]);

        return new WorkflowEditorControl(workflow, save: () => { }, host, new RunStore(new InMemoryPluginStorage()), []);
    }

    private static Color _Token(string key) =>
        (Color)(Application.Current?.FindResource(key) ?? throw new InvalidOperationException($"no token '{key}'"));
}

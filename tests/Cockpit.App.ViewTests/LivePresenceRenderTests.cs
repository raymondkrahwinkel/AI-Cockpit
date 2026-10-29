using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Cockpit.App.Files;
using Cockpit.App.Views.Editor;
using Cockpit.Plugins.Abstractions.Files;

namespace Cockpit.App.ViewTests;

// AC-1900 (mockup AC-1895 v2, screens 4 and 4p): other people's carets, selections and avatars are drawn in the colour the source sent.
[Collection("avalonia")]
public sealed class LivePresenceRenderTests
{
    private const string Text = "# Weekly sync\n\nJint runs markdown.ts in the Depot plugin, not in the host\n\n- [ ] Joris: measure Jint start-up on Linux\n";

    // Layer 2, view: the flag, the selection and the avatar ring show exactly the colour in LivePeer.Color (none of them is worked out
    // from the name), an agent's avatar is a rounded square, and a peer without a caret gets an avatar but no flag.
    [Fact]
    public void PeersAreDrawnInTheirServerColour_NeverOneWorkedOutHere() => HeadlessAvalonia.Run(() =>
    {
        var sanne = Text.IndexOf("not in the host", StringComparison.Ordinal);
        var joris = Text.IndexOf("on Linux", StringComparison.Ordinal) + "on Linux".Length;
        var editor = new MarkdownEditor { Text = Text, Margin = new Thickness(0, 40, 0, 0) };
        var bar = new LivePresenceBar { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top, Margin = new Thickness(20) };
        var window = new Window { Width = 900, Height = 420, Content = new Grid { Children = { editor, bar } } };
        window.Show();
        try
        {
            IReadOnlyList<LivePeer> peers =
            [
                new("raymond", "Raymond", "#3b82f6", "Cockpit", "Editor", "editing", You: true),
                new("sanne", "Sanne de Vries", "#3fb8c4", "web", "Editor", "selecting", Caret: sanne + 15, Selection: new LiveRange(sanne, sanne + 15)),
                new("joris", "Joris Bakker", "#d9a441", "Cockpit", "Editor", "editing", Caret: joris),
                new("assistant", "Assistant", "#a78bda", "agent", "Editor", "editing"),
            ];
            editor.SetPeers(peers);
            bar.SetPeers(peers);
            window.UpdateLayout();
            var textView = editor.TextEditor.TextArea.TextView;
            var sanneAt = _Visual(window, textView, editor.TextEditor.Document.GetLocation(sanne + 15));
            var jorisAt = _Visual(window, textView, editor.TextEditor.Document.GetLocation(joris));
            var selectionAt = _Visual(window, textView, editor.TextEditor.Document.GetLocation(sanne + 4));

            // The flag's left padding, 8 px above the caret top: the flag is the only thing drawn there.
            Assert.Equal(Color.Parse("#3fb8c4"), RenderedScene.PaintedAt(window, new Point(sanneAt.X + 2, sanneAt.Y - 8)));
            Assert.Equal(Color.Parse("#d9a441"), RenderedScene.PaintedAt(window, new Point(jorisAt.X + 2, jorisAt.Y - 8)));

            // About 28% of the colour over the surface beside the selection.
            var beside = RenderedScene.PaintedAt(window, new Point(selectionAt.X - 40, selectionAt.Y - 1));
            var inside = RenderedScene.PaintedAt(window, new Point(selectionAt.X, selectionAt.Y - 1));
            Assert.InRange(inside.G - beside.G, (0xb8 - beside.G) * 0.28 - 3, (0xb8 - beside.G) * 0.28 + 3);

            var avatars = bar.GetVisualDescendants().OfType<Grid>().Where(grid => grid.Width == 22).ToList();
            Assert.Equal(4, avatars.Count);
            var round = _Origin(window, avatars[1]);
            var square = _Origin(window, avatars[3]);
            _Is("#3fb8c4", RenderedScene.PaintedAt(window, new Point(round.X - 1, round.Y + 11)));
            _Is("#a78bda", RenderedScene.PaintedAt(window, new Point(square.X - 1, square.Y + 11)));
            Assert.NotEqual(Color.Parse("#a78bda"), RenderedScene.PaintedAt(window, new Point(round.X + 1, round.Y + 1)));
            _Is("#a78bda", RenderedScene.PaintedAt(window, new Point(square.X + 1, square.Y + 1)));
        }
        finally
        {
            window.Close();
        }
    });

    // Within a few steps of the colour: a ring is anti-aliased against what is under it, a flag is not.
    private static void _Is(string expected, Color actual)
    {
        var want = Color.Parse(expected);
        Assert.InRange(Math.Abs(want.R - actual.R) + Math.Abs(want.G - actual.G) + Math.Abs(want.B - actual.B), 0, 40);
    }

    // The top-left of the caret's text, in window coordinates.
    private static Point _Visual(Window window, AvaloniaEdit.Rendering.TextView textView, AvaloniaEdit.Document.TextLocation location)
    {
        var at = textView.GetVisualPosition(new TextViewPosition(location), AvaloniaEdit.Rendering.VisualYPosition.TextTop) - textView.ScrollOffset;
        return textView.TranslatePoint(at, window) ?? throw new InvalidOperationException("The text view is not in the window.");
    }

    private static Point _Origin(Window window, Control control) =>
        control.TranslatePoint(default, window) ?? throw new InvalidOperationException("The avatar is not in the window.");
}

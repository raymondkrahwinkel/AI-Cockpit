using System.Globalization;
using Avalonia;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Cockpit.Plugins.Abstractions.Files;

namespace Cockpit.App.Views.Editor;

public sealed partial class MarkdownEditor
{
    // Same on both themes: the flag text of Depot's web editor, drawn on the colour the source chose.
    private static readonly IBrush FlagText = new SolidColorBrush(Color.Parse("#0b0d12"));
    private List<RemoteCursor> _remote = [];

    // Other people's carets and selections. Offsets ride TextAnchors, so typing here moves them before the next update arrives.
    // The colour is the source's own; a peer without a valid colour is not drawn, because the host never picks one.
    internal void SetPeers(IReadOnlyList<LivePeer> peers)
    {
        var document = _editor.Document;
        var next = new List<RemoteCursor>();
        foreach (var peer in peers)
        {
            if (peer.You || peer.Caret is null || !Color.TryParse(peer.Color, out var color))
            {
                continue;
            }

            var selection = peer.Selection is { } range && range.End > range.Start
                ? (document.CreateAnchor(_Clamp(range.Start)), document.CreateAnchor(_Clamp(range.End)))
                : ((TextAnchor, TextAnchor)?)null;
            next.Add(new RemoteCursor(peer.Name, color, peer.Away, document.CreateAnchor(_Clamp(peer.Caret.Value)), selection));
        }

        _remote = next;
        var textView = _editor.TextArea.TextView;
        textView.InvalidateLayer(KnownLayer.Selection);
        textView.InvalidateLayer(KnownLayer.Caret);
        return;

        int _Clamp(int offset) => Math.Clamp(offset, 0, document.TextLength);
    }

    private sealed record RemoteCursor(string Name, Color Color, bool Away, TextAnchor Caret, (TextAnchor Start, TextAnchor End)? Selection);

    // Two layers of one renderer: the selection under the text, the caret and its name flag over it.
    private sealed class RemoteCursors(MarkdownEditor owner, KnownLayer layer) : IBackgroundRenderer
    {
        public KnownLayer Layer => layer;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (!textView.VisualLinesValid)
            {
                return;
            }

            foreach (var cursor in owner._remote)
            {
                using (drawingContext.PushOpacity(cursor.Away ? 0.35 : 1))
                {
                    if (layer == KnownLayer.Selection)
                    {
                        _Selection(textView, drawingContext, cursor);
                    }
                    else
                    {
                        _Caret(textView, drawingContext, cursor);
                    }
                }
            }
        }

        // The person colour at about 28%, with a 2 px radius, like the web editor.
        private static void _Selection(TextView textView, DrawingContext drawingContext, RemoteCursor cursor)
        {
            if (cursor.Selection is not { } range || range.Start.IsDeleted || range.End.IsDeleted)
            {
                return;
            }

            var builder = new BackgroundGeometryBuilder { CornerRadius = 2 };
            builder.AddSegment(textView, new TextSegment { StartOffset = range.Start.Offset, EndOffset = range.End.Offset });
            if (builder.CreateGeometry() is { } geometry)
            {
                drawingContext.DrawGeometry(new SolidColorBrush(cursor.Color, 0.28), null, geometry);
            }
        }

        // A 2 px caret the height of its text, and above it the first name: 600 10.5/16 px, padding 0 5, radius 4 4 4 0.
        private static void _Caret(TextView textView, DrawingContext drawingContext, RemoteCursor cursor)
        {
            if (cursor.Caret.IsDeleted || textView.Document is not { } document)
            {
                return;
            }

            var location = document.GetLocation(cursor.Caret.Offset);
            if (textView.GetVisualLine(location.Line) is null)
            {
                return;
            }

            var position = new TextViewPosition(location);
            var top = textView.GetVisualPosition(position, VisualYPosition.TextTop) - textView.ScrollOffset;
            var bottom = textView.GetVisualPosition(position, VisualYPosition.TextBottom) - textView.ScrollOffset;
            var brush = new SolidColorBrush(cursor.Color);
            drawingContext.FillRectangle(brush, new Rect(Math.Round(top.X) - 1, top.Y, 2, bottom.Y - top.Y));

            var name = new FormattedText(cursor.Name.Split(' ')[0], CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, weight: FontWeight.SemiBold), 10.5, FlagText);
            var flag = new Rect(Math.Round(top.X) - 1, Math.Max(0, top.Y - 16), name.Width + 10, 16);
            drawingContext.DrawRectangle(brush, null, new RoundedRect(flag, new CornerRadius(4, 4, 4, 0)));
            drawingContext.DrawText(name, new Point(flag.X + 5, flag.Y + (16 - name.Height) / 2));
        }
    }
}

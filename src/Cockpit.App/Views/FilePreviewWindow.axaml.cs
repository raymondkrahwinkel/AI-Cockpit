using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Cockpit.App.Controls;
using Cockpit.App.Services;
using Cockpit.App.Theming;
using Cockpit.Infrastructure.Pdf;
using Cockpit.Infrastructure.Svg;

namespace Cockpit.App.Views;

// AC-642: window a clickable code-span path opens onto — image, source file (jump to line),
// rendered markdown/json/csv, a one-level directory walk, or a plain header; see
// FilePreviewClassifier. Reading/classifying always happens off this thread; only the result returns.
public partial class FilePreviewWindow : Window
{
    // "1 MB tekst is ruim" — a cap on what gets read, not a cap most files ever reach.
    private const long MaxTextBytes = 1024 * 1024;
    private const int MaxListedRows = 500;
    private const float SvgRasterSize = 1600f;

    // AC-730: Ctrl+scroll zoom range for image/SVG (later PDF) previews, own 10-800% range.
    private const double MinZoom = 0.10;
    private const double MaxZoom = 8.0;
    private const double ZoomStepBase = 1.1;

    // Same fallback list as MarkdownView's own MonoFont — a font resource lookup needs live Application
    // resources, and this has to render in a headless test harness too.
    private static readonly FontFamily MonoFont = new("Cascadia Mono, Noto Sans Mono, DejaVu Sans Mono, monospace");

    // Remembered for the app session only (not `cockpit.json` — see the ticket's "Meeschalen" section): the
    // next preview opens at the size the operator last dragged this one to.
    private static Size? _lastSize;

    private readonly Stack<string> _history = new();
    private string _path = string.Empty;
    private Bitmap? _bitmap;
    private Image? _previewImage;
    private double _zoom = 1.0;

    // The in-flight (or last completed) navigation — every fire-and-forget call site below assigns it, so a
    // test can await the real condition ("this navigation landed") instead of a fixed sleep it hopes is long
    // enough (AC-991).
    private Task _navigationTask = Task.CompletedTask;

    // Exposed for tests only: awaiting the window itself would need a public event, this is the same fact.
    internal Task WaitForIdleAsync() => _navigationTask;

    public FilePreviewWindow()
    {
        InitializeComponent();
        CockpitWindowChrome.Apply(this, "Voorbeeld");
        SizeChanged += (_, e) => _lastSize = e.NewSize;

        // Tunnel + handledEventsToo, same as ImagePreviewWindow (AC-778): this must see the wheel before
        // BodyScroller's own presenter claims it for scrolling. Only Ctrl+scroll is intercepted, so plain
        // scroll still scrolls a code preview taller than the window.
        BodyScroller.AddHandler(InputElement.PointerWheelChangedEvent, _OnBodyWheel, RoutingStrategies.Tunnel, handledEventsToo: true);
        KeyDown += _OnKeyDown;
    }

    public static void Show(string path, int? line, Window owner) => Build(path, line).Show(owner);

    // The render harness's own step (the same split ScreenshotPreviewWindow.Build makes): the window built,
    // navigating to its first path, without being put on screen.
    internal static FilePreviewWindow Build(string path, int? line)
    {
        var window = new FilePreviewWindow();
        if (_lastSize is { } size)
        {
            window.Width = size.Width;
            window.Height = size.Height;
        }

        window._navigationTask = window._NavigateAsync(path, line);
        return window;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _bitmap?.Dispose();
        _bitmap = null;
    }

    private void _OnBack(object? sender, RoutedEventArgs e)
    {
        if (_history.Count > 0)
        {
            _navigationTask = _NavigateAsync(_history.Pop(), null, recordHistory: false);
        }
    }

    private void _OnUp(object? sender, RoutedEventArgs e)
    {
        if (Path.GetDirectoryName(_path) is { Length: > 0 } parent)
        {
            _navigationTask = _NavigateAsync(parent, null);
        }
    }

    private void _OnCopyPath(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            _ = clipboard.SetTextAsync(_path);
        }
    }

    private void _OnOpen(object? sender, RoutedEventArgs e) => ExternalLink.TryOpenWithSystemApp(_path);

    // .html/.htm always classify as FilePreviewKind.Text (LooksLikeText), so this checks the extension
    // directly rather than adding a kind only this button would ever read.
    private static bool _IsHtml(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".html", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".htm", StringComparison.OrdinalIgnoreCase);
    }

    // Ctrl+scroll zooms an image/SVG/PDF preview; plain scroll is left alone so BodyScroller still scrolls a
    // code preview taller than the window. No-op when the current body is not an image (_previewImage null).
    private void _OnBodyWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_previewImage is null || !e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            return;
        }

        e.Handled = true;
        _ApplyZoom(_zoom * Math.Pow(ZoomStepBase, e.Delta.Y));
    }

    private void _OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_previewImage is not null && e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key is Key.D0 or Key.NumPad0)
        {
            _ApplyZoom(1.0);
            e.Handled = true;
        }
    }

    private void _OnImageDoubleTapped(object? sender, TappedEventArgs e) => _ApplyZoom(1.0);

    private void _ApplyZoom(double zoom)
    {
        if (_previewImage is null)
        {
            return;
        }

        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        var transform = (ScaleTransform)_previewImage.RenderTransform!;
        transform.ScaleX = _zoom;
        transform.ScaleY = _zoom;
    }

    // A directory row or a "back"/"up" jump lands here too — same load, same classification, same body switch;
    // a directory is just another kind this window can show.
    private async Task _NavigateAsync(string path, int? line, bool recordHistory = true)
    {
        if (recordHistory && _path.Length > 0)
        {
            _history.Push(_path);
        }

        _path = path;
        BackButton.IsVisible = _history.Count > 0;

        var loaded = await Task.Run(() => _Load(path));

        _bitmap?.Dispose();
        _bitmap = loaded.Bitmap;

        NameText.Text = loaded.Name;
        KindText.Text = loaded.Kind == FilePreviewKind.Text && line is { } atLine
            ? $"code · regel {atLine}"
            : loaded.KindLabel;
        PathText.Text = path;
        MetaText.Text = loaded.Meta;
        OpenButton.IsVisible = loaded.Kind != FilePreviewKind.Missing;
        OpenInBrowserButton.IsVisible = loaded.Kind != FilePreviewKind.Missing && _IsHtml(path);
        UpButton.IsVisible = loaded.Kind == FilePreviewKind.Directory;

        BodyHost.Content = _BuildBody(loaded, line);
    }

    private sealed record _DirectoryEntry(string Name, bool IsDirectory, long Size, DateTimeOffset Modified);

    private sealed record _Loaded(
        FilePreviewKind Kind,
        string Name,
        string KindLabel,
        string Meta,
        Bitmap? Bitmap,
        string? Text,
        bool Truncated,
        IReadOnlyList<_DirectoryEntry>? Entries);

    private static _Loaded _Load(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Length == 0)
        {
            name = path;
        }

        if (Directory.Exists(path))
        {
            var entries = _ListDirectory(path);
            return new _Loaded(FilePreviewKind.Directory, name, "map", $"{entries.Count} items", null, null, false, entries);
        }

        if (!File.Exists(path))
        {
            return new _Loaded(FilePreviewKind.Missing, name, "niet gevonden", "—", null, null, false, null);
        }

        var info = new FileInfo(path);
        var head = new byte[Math.Min(8192L, info.Length)];
        using (var headStream = File.OpenRead(path))
        {
            _ = headStream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        }

        var kind = FilePreviewClassifier.Classify(path, head);
        var meta = _FormatMeta(info);

        if (kind == FilePreviewKind.Svg)
        {
            var rasterized = SvgRasterizer.Rasterize(File.ReadAllBytes(path), SvgRasterSize);
            if (rasterized is not null)
            {
                using var stream = new MemoryStream(rasterized);
                return new _Loaded(FilePreviewKind.Svg, name, "svg", meta, new Bitmap(stream), null, false, null);
            }

            // Malformed SVG: it is XML either way, so fall through to a plain text read below.
            kind = FilePreviewKind.Text;
        }

        if (kind == FilePreviewKind.Image)
        {
            using var stream = File.OpenRead(path);
            var bitmap = new Bitmap(stream);
            var pixelMeta = $"{bitmap.PixelSize.Width} × {bitmap.PixelSize.Height} px · {meta}";
            return new _Loaded(FilePreviewKind.Image, name, "afbeelding", pixelMeta, bitmap, null, false, null);
        }

        if (kind == FilePreviewKind.Pdf)
        {
            var rasterized = PdfRasterizer.Rasterize(File.ReadAllBytes(path));
            if (rasterized.Png is { } png)
            {
                using var stream = new MemoryStream(png);
                var pages = rasterized.PageCount == 1 ? "1 pagina" : $"{rasterized.PageCount} pagina's";
                return new _Loaded(FilePreviewKind.Pdf, name, "pdf", $"{pages} · {meta}", new Bitmap(stream), null, false, null);
            }

            // Encrypted/corrupt: falls back to the existing Other card below, but the reason still reaches the
            // operator through the meta line above it rather than a blank pane (AC-730 acceptance criterion 6).
            return new _Loaded(FilePreviewKind.Other, name, "pdf", $"{meta} · kon niet worden geopend: {rasterized.Error}", null, null, false, null);
        }

        // Other (video, exe, archief, ...) gets no preview at all — reading it as text would UTF-8-decode
        // binary bytes into replacement characters and show that as "code", which is worse than the plain
        // "no preview" state _OtherBody already draws.
        if (kind == FilePreviewKind.Other)
        {
            return new _Loaded(FilePreviewKind.Other, name, "bestand", meta, null, null, false, null);
        }

        var truncated = info.Length > MaxTextBytes;
        var text = _ReadCappedText(path, MaxTextBytes);

        return kind switch
        {
            FilePreviewKind.Json => new _Loaded(FilePreviewKind.Json, name, "json", meta, null, _FormatJson(text), truncated, null),
            FilePreviewKind.Csv => new _Loaded(FilePreviewKind.Csv, name, "csv", meta, null, _CsvToMarkdownTable(text, truncated), false, null),
            FilePreviewKind.Markdown => new _Loaded(FilePreviewKind.Markdown, name, "markdown", meta, null, text, truncated, null),
            FilePreviewKind.Text => new _Loaded(FilePreviewKind.Text, name, "code", meta, null, text, truncated, null),
            _ => throw new InvalidOperationException($"{kind} should have returned earlier in _Load"),
        };
    }

    private static string _ReadCappedText(string path, long maxBytes)
    {
        using var stream = File.OpenRead(path);
        var length = Math.Min(stream.Length, maxBytes);
        var buffer = new byte[length];
        _ = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        return Encoding.UTF8.GetString(buffer);
    }

    private static List<_DirectoryEntry> _ListDirectory(string path)
    {
        var entries = new List<_DirectoryEntry>();
        foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos().Take(MaxListedRows))
        {
            var isDirectory = entry is DirectoryInfo;
            entries.Add(new _DirectoryEntry(entry.Name, isDirectory, isDirectory ? 0 : ((FileInfo)entry).Length, entry.LastWriteTime));
        }

        entries.Sort((a, b) => a.IsDirectory != b.IsDirectory
            ? b.IsDirectory.CompareTo(a.IsDirectory)
            : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return entries;
    }

    // Indented if it parses; a kapotte JSON wíl je juist rauw zien, so a parse failure is not an error state —
    // it just means the file renders as plain text instead.
    private static string _FormatJson(string text)
    {
        try
        {
            return JsonNode.Parse(text)?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? text;
        }
        catch (JsonException)
        {
            return text;
        }
    }

    // Reused all the way down: this builds a markdown pipe-table string and hands it to the same MarkdownView
    // that already renders one in the transcript — no second table implementation.
    private static string _CsvToMarkdownTable(string text, bool truncated)
    {
        var rows = _ParseCsv(text);
        if (rows.Count == 0)
        {
            return "*(leeg)*";
        }

        const int maxRows = 300;
        var shown = rows.Take(maxRows + 1).ToList();
        var builder = new StringBuilder();
        builder.Append('|').AppendJoin('|', shown[0].Select(_EscapeCell)).Append('|').Append('\n');
        builder.Append('|').AppendJoin('|', shown[0].Select(_ => "---")).Append('|').Append('\n');
        foreach (var row in shown.Skip(1).Take(maxRows))
        {
            builder.Append('|').AppendJoin('|', row.Select(_EscapeCell)).Append('|').Append('\n');
        }

        if (rows.Count - 1 > maxRows || truncated)
        {
            builder.Append($"\n*(afgekapt — {rows.Count - 1} rijen in het bestand)*");
        }

        return builder.ToString();
    }

    private static string _EscapeCell(string cell) => cell.Replace("|", "\\|", StringComparison.Ordinal);

    // A minimal RFC 4180 reader: quoted fields, doubled quotes as an escaped quote, commas inside quotes.
    private static List<List<string>> _ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    inQuotes = false;
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    rows.Add(row);
                    row = [];
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }

    private Control _BuildBody(_Loaded loaded, int? line)
    {
        // Reset for every navigation: the previous body's Image (if any) is about to be discarded, and a
        // non-image body must leave Ctrl+scroll/Ctrl+0 as no-ops rather than reaching into a stale reference.
        _previewImage = null;
        _zoom = 1.0;

        return loaded.Kind switch
        {
            FilePreviewKind.Image or FilePreviewKind.Svg or FilePreviewKind.Pdf => _ImageBody(loaded.Bitmap!),
            FilePreviewKind.Markdown or FilePreviewKind.Csv => new MarkdownView { Markdown = loaded.Text },
            FilePreviewKind.Json or FilePreviewKind.Text => _CodeBody(loaded.Text ?? string.Empty, line),
            FilePreviewKind.Directory => _DirectoryBody(loaded.Entries ?? []),
            FilePreviewKind.Missing => _MissingBody(),
            _ => _OtherBody(),
        };
    }

    private Control _ImageBody(Bitmap bitmap)
    {
        var image = new Image
        {
            Source = bitmap,
            Stretch = Stretch.Uniform,
            MaxWidth = bitmap.PixelSize.Width,
            MaxHeight = bitmap.PixelSize.Height,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = new ScaleTransform(1, 1),
        };
        image.DoubleTapped += _OnImageDoubleTapped;
        _previewImage = image;

        return new Border
        {
            Background = CheckerboardBrush(),
            Padding = new Thickness(16),
            Child = image,
        };
    }

    // AC-642: tiled two-tone pattern behind an image so transparency reads as transparency, on
    // theme tokens like the rest of the chrome. Internal: AC-778's ImagePreviewWindow reuses it.
    internal static IBrush CheckerboardBrush()
    {
        const double cell = 8;
        var light = new GeometryGroup { FillRule = FillRule.NonZero };
        light.Children.Add(new RectangleGeometry(new Rect(0, 0, cell, cell)));
        light.Children.Add(new RectangleGeometry(new Rect(cell, cell, cell, cell)));

        var drawing = new DrawingGroup
        {
            Children =
            {
                new GeometryDrawing
                {
                    Brush = ThemeBrush.Resolve("CockpitSecondaryBgBrush", "#0c0e12"),
                    Geometry = new RectangleGeometry(new Rect(0, 0, cell * 2, cell * 2)),
                },
                new GeometryDrawing
                {
                    Brush = ThemeBrush.Resolve("CockpitInsetBgBrush", "#202430"),
                    Geometry = light,
                },
            },
        };

        return new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            Stretch = Stretch.None,
            DestinationRect = new RelativeRect(0, 0, cell * 2, cell * 2, RelativeUnit.Absolute),
        };
    }

    // Monospace, scrollable, with line numbers — a fenced code block's own look, minus syntax colour (AC-642:
    // no editor component lives in this app, and this ticket does not add one).
    private Control _CodeBody(string text, int? line)
    {
        var lines = text.Split('\n');
        var gutter = new StringBuilder();
        for (var i = 1; i <= lines.Length; i++)
        {
            gutter.Append(i).Append('\n');
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(12) };

        var gutterText = new SelectableTextBlock
        {
            Text = gutter.ToString().TrimEnd('\n'),
            FontFamily = MonoFont,
            FontSize = 12.5,
            Foreground = ThemeBrush.Resolve("CockpitTextFaintBrush", "#656c78"),
            Margin = new Thickness(0, 0, 12, 0),
            TextAlignment = Avalonia.Media.TextAlignment.Right,
        };
        Grid.SetColumn(gutterText, 0);
        grid.Children.Add(gutterText);

        var sourceText = new SelectableTextBlock
        {
            Text = text.TrimEnd('\n'),
            FontFamily = MonoFont,
            FontSize = 12.5,
            Foreground = ThemeBrush.Resolve("CockpitTextPrimaryBrush", "#e8eaef"),
            TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
        };
        Grid.SetColumn(sourceText, 1);
        grid.Children.Add(sourceText);

        if (line is { } target && target >= 1 && target <= lines.Length)
        {
            // Best-effort scroll: the exact row height depends on the resolved font, so this estimates rather
            // than measures — close enough to land the target line in view, not pixel-exact.
            const double estimatedRowHeight = 18.0;
            Dispatcher.UIThread.Post(
                () => BodyScroller.Offset = new Vector(0, Math.Max(0, (target - 5) * estimatedRowHeight)),
                DispatcherPriority.Loaded);
        }

        return grid;
    }

    private Control _DirectoryBody(IReadOnlyList<_DirectoryEntry> entries)
    {
        var panel = new StackPanel { Spacing = 1, Margin = new Thickness(6) };
        foreach (var entry in entries)
        {
            var fullPath = Path.Combine(_path, entry.Name);
            var row = new Border
            {
                Padding = new Thickness(10, 6),
                Cursor = new Cursor(StandardCursorType.Hand),
                Background = Brushes.Transparent,
            };

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
            grid.Children.Add(new TextBlock
            {
                Text = entry.IsDirectory ? $"{entry.Name}/" : entry.Name,
                FontFamily = MonoFont,
                FontSize = 12.5,
                Foreground = ThemeBrush.Resolve("CockpitTextPrimaryBrush", "#e8eaef"),
            });

            var sizeText = new TextBlock
            {
                Text = entry.IsDirectory ? string.Empty : _FormatSize(entry.Size),
                FontSize = 11,
                Margin = new Thickness(12, 0),
                Foreground = ThemeBrush.Resolve("CockpitTextFaintBrush", "#656c78"),
            };
            Grid.SetColumn(sizeText, 1);
            grid.Children.Add(sizeText);

            var dateText = new TextBlock
            {
                Text = entry.Modified.ToString("yyyy-MM-dd HH:mm"),
                FontSize = 11,
                Foreground = ThemeBrush.Resolve("CockpitTextFaintBrush", "#656c78"),
            };
            Grid.SetColumn(dateText, 2);
            grid.Children.Add(dateText);

            row.Child = grid;
            row.PointerPressed += (_, _) => _navigationTask = _NavigateAsync(fullPath, null);
            panel.Children.Add(row);
        }

        return panel;
    }

    private Control _MissingBody() => new StackPanel
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Spacing = 4,
        Margin = new Thickness(24),
        Children =
        {
            new TextBlock
            {
                Text = "Dit bestand staat hier niet.",
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = ThemeBrush.Resolve("CockpitTextPrimaryBrush", "#e8eaef"),
            },
            new TextBlock
            {
                Text = "Weggehaald, of het staat op de machine waar de sessie draait.",
                FontSize = 11.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Foreground = ThemeBrush.Resolve("CockpitTextFaintBrush", "#656c78"),
            },
        },
    };

    private static Control _OtherBody() => new TextBlock
    {
        Text = "Geen voorbeeld voor dit bestandstype — gebruik Openen hieronder.",
        FontSize = 12.5,
        Margin = new Thickness(24),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = ThemeBrush.Resolve("CockpitTextFaintBrush", "#656c78"),
    };

    private static string _FormatMeta(FileInfo info) => $"{_FormatSize(info.Length)} · {_FormatDate(info.LastWriteTime)}";

    private static string _FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB",
    };

    private static string _FormatDate(DateTime value) => value.ToString("yyyy-MM-dd HH:mm");
}

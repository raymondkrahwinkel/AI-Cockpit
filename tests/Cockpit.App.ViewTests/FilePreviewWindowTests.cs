using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

// The window a clickable path opens (AC-642), exercised through the same `Build` seam `ScreenshotPreviewWindow`
// uses — the harness's own step, not a real mouse click through `MarkdownView`. One case per soort from
// criterion 13, plus resize (criterion 7).
[Collection("avalonia")]
public sealed class FilePreviewWindowTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cockpit-preview-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string _KindText(Window window) =>
        window.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Name == "KindText").Text ?? string.Empty;

    private static bool _OpenButtonVisible(Window window) =>
        window.GetLogicalDescendants().OfType<Button>().First(b => b.Name == "OpenButton").IsVisible;

    private static Control _Body(Window window) =>
        (Control)window.GetLogicalDescendants().OfType<ContentControl>().First(c => c.Name == "BodyHost").Content!;

    [Fact]
    public async Task MissingFile_ShowsNotFoundStateAndHidesOpen()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var window = FilePreviewWindow.Build(Path.Combine(_dir, "ghost.txt"), null);
            await Task.Delay(200);

            Assert.Equal("niet gevonden", _KindText(window));
            Assert.False(_OpenButtonVisible(window));
        });
    }

    [Fact]
    public async Task TextFile_ShowsCodeBadgeWithLineNumberAndKeepsOpenVisible()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var path = Path.Combine(_dir, "Foo.cs");
            await File.WriteAllTextAsync(path, "line one\nline two\nline three");

            var window = FilePreviewWindow.Build(path, 2);
            await Task.Delay(200);

            Assert.Equal("code · regel 2", _KindText(window));
            Assert.True(_OpenButtonVisible(window));
        });
    }

    [Fact]
    public async Task ImageFile_RendersAnImageOnACheckerboard()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var path = Path.Combine(_dir, "pic.png");
            await File.WriteAllBytesAsync(path, _TinyPng());

            var window = FilePreviewWindow.Build(path, null);
            await Task.Delay(200);

            Assert.Equal("afbeelding", _KindText(window));
            var border = Assert.IsType<Border>(_Body(window));
            Assert.IsType<Image>(border.Child);
        });
    }

    [Fact]
    public async Task BinaryFile_ShowsNoPreviewInsteadOfDecodedGarbageAsCode()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            // A NUL byte in the head rules out "text" in FilePreviewClassifier — Other, not Text. No recognised
            // extension, so this exercises the LooksLikeText fallback rather than an extension-based kind.
            var path = Path.Combine(_dir, "report.bin");
            await File.WriteAllBytesAsync(path, [0x00, 0x01, 0x02, 0x03]);

            var window = FilePreviewWindow.Build(path, null);
            await Task.Delay(200);

            Assert.Equal("bestand", _KindText(window));
            var text = Assert.IsType<TextBlock>(_Body(window));
            Assert.Equal("Geen voorbeeld voor dit bestandstype — gebruik Openen hieronder.", text.Text);
        });
    }

    // AC-730 acceptance criterion 5: a valid PDF renders page 1 through the same _ImageBody as Image/Svg, with
    // the page count in the meta line.
    [Fact]
    public async Task PdfFile_ShowsPageOneAsImageWithPageCountInMeta()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var path = Path.Combine(_dir, "doc.pdf");
            await File.WriteAllBytesAsync(path, _MinimalOnePagePdf());

            var window = FilePreviewWindow.Build(path, null);
            await Task.Delay(200);

            Assert.Equal("pdf", _KindText(window));
            var meta = window.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Name == "MetaText").Text ?? string.Empty;
            Assert.Contains("1 pagina", meta, StringComparison.Ordinal);
            var border = Assert.IsType<Border>(_Body(window));
            Assert.IsType<Image>(border.Child);
        });
    }

    // AC-730 acceptance criterion 6: an encrypted/corrupt PDF must fall back to the Other card with the reason
    // in the meta line, not a blank pane or a crash.
    [Fact]
    public async Task CorruptPdf_FallsBackToOtherCardWithReasonInMeta()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var path = Path.Combine(_dir, "report.pdf");
            await File.WriteAllBytesAsync(path, [0x25, 0x50, 0x44, 0x46, 0x00, 0x01, 0x02, 0x03]);

            var window = FilePreviewWindow.Build(path, null);
            await Task.Delay(200);

            Assert.Equal("pdf", _KindText(window));
            var meta = window.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Name == "MetaText").Text ?? string.Empty;
            Assert.Contains("kon niet worden geopend", meta, StringComparison.Ordinal);
            var text = Assert.IsType<TextBlock>(_Body(window));
            Assert.Equal("Geen voorbeeld voor dit bestandstype — gebruik Openen hieronder.", text.Text);
        });
    }

    [Fact]
    public async Task HtmlFile_ShowsCodeByDefaultWithAnOpenInBrowserButton()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var path = Path.Combine(_dir, "page.html");
            await File.WriteAllTextAsync(path, "<html><body>hi</body></html>");

            var window = FilePreviewWindow.Build(path, null);
            await Task.Delay(200);

            Assert.Equal("code", _KindText(window));
            Assert.IsType<Grid>(_Body(window));
            var openInBrowser = window.GetLogicalDescendants().OfType<Button>().First(b => b.Name == "OpenInBrowserButton");
            Assert.True(openInBrowser.IsVisible);
        });
    }

    [Fact]
    public async Task TextFile_HidesOpenInBrowserButton()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var path = Path.Combine(_dir, "Foo.cs");
            await File.WriteAllTextAsync(path, "line one");

            var window = FilePreviewWindow.Build(path, null);
            await Task.Delay(200);

            var openInBrowser = window.GetLogicalDescendants().OfType<Button>().First(b => b.Name == "OpenInBrowserButton");
            Assert.False(openInBrowser.IsVisible);
        });
    }

    [Fact]
    public async Task JsonFile_IsIndented()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var path = Path.Combine(_dir, "data.json");
            await File.WriteAllTextAsync(path, """{"a":1,"b":[2,3]}""");

            var window = FilePreviewWindow.Build(path, null);
            await Task.Delay(200);

            Assert.Equal("json", _KindText(window));
            var grid = Assert.IsType<Grid>(_Body(window));
            var text = grid.Children.OfType<SelectableTextBlock>().Last().Text ?? string.Empty;
            Assert.Contains("\n", text, StringComparison.Ordinal); // indented, not the single-line source
        });
    }

    [Fact]
    public async Task Directory_ListsEntriesDirectoriesFirstAndNavigatesOnClick()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            Directory.CreateDirectory(Path.Combine(_dir, "zzz-sub"));
            await File.WriteAllTextAsync(Path.Combine(_dir, "aaa.txt"), "hi");

            var window = FilePreviewWindow.Build(_dir, null);
            window.Show();
            await Task.Delay(200);

            Assert.Equal("map", _KindText(window));
            var panel = Assert.IsType<StackPanel>(_Body(window));
            Assert.Equal(2, panel.Children.Count);

            var firstRow = (Border)panel.Children[0];
            var firstRowNameText = ((Grid)firstRow.Child!).Children.OfType<TextBlock>().First().Text;
            Assert.Equal("zzz-sub/", firstRowNameText); // directories sort before files

            var point = firstRow.TranslatePoint(new Point(firstRow.Bounds.Width / 2, firstRow.Bounds.Height / 2), window)
                ?? throw new InvalidOperationException("the row must be laid out inside the window to be clicked");
            window.MouseDown(point, MouseButton.Left);
            await Task.Delay(200);

            Assert.Equal("map", _KindText(window)); // navigated into zzz-sub, itself an empty directory
            window.Close();
        });
    }

    [Fact]
    public async Task Resize_GrowsTheBody()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var path = Path.Combine(_dir, "Foo.cs");
            await File.WriteAllTextAsync(path, "one\ntwo\nthree");

            var window = FilePreviewWindow.Build(path, null);
            window.Show();
            await Task.Delay(200);

            var bodyHost = window.GetLogicalDescendants().OfType<ContentControl>().First(c => c.Name == "BodyHost");
            var before = bodyHost.Bounds.Width;

            window.Width += 300;
            await Task.Delay(100);

            Assert.True(bodyHost.Bounds.Width > before);
            window.Close();
        });
    }

    // Ctrl+scroll zoom's only branchy logic is the clamp — invoked directly since Avalonia's headless input
    // harness has no way to raise a wheel event carrying `KeyModifiers.Control` (same approach as AC-778's
    // ImagePreviewWindowTests.CtrlScrollZoom_ClampsToConfiguredRange).
    [Fact]
    public async Task CtrlScrollZoom_ClampsToConfiguredRange()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var path = Path.Combine(_dir, "pic.png");
            await File.WriteAllBytesAsync(path, _TinyPng());

            var window = FilePreviewWindow.Build(path, null);
            await Task.Delay(200);

            var applyZoom = typeof(FilePreviewWindow).GetMethod("_ApplyZoom", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var image = (Image)((Border)_Body(window)).Child!;
            var transform = (ScaleTransform)image.RenderTransform!;

            applyZoom.Invoke(window, [100.0]);
            Assert.Equal(8.0, transform.ScaleX);

            applyZoom.Invoke(window, [0.001]);
            Assert.Equal(0.10, transform.ScaleX);
        });
    }

    [Fact]
    public async Task Zoom_ResetsToActualSizeOnDoubleTapAndOnLoadingAnotherFile()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var path = Path.Combine(_dir, "pic.png");
            await File.WriteAllBytesAsync(path, _TinyPng());

            var window = FilePreviewWindow.Build(path, null);
            await Task.Delay(200);

            var applyZoom = typeof(FilePreviewWindow).GetMethod("_ApplyZoom", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var doubleTap = typeof(FilePreviewWindow).GetMethod("_OnImageDoubleTapped", BindingFlags.NonPublic | BindingFlags.Instance)!;
            applyZoom.Invoke(window, [4.0]);

            doubleTap.Invoke(window, [null, null]);
            var transform = (ScaleTransform)((Image)((Border)_Body(window)).Child!).RenderTransform!;
            Assert.Equal(1.0, transform.ScaleX);

            applyZoom.Invoke(window, [4.0]);
            var navigate = typeof(FilePreviewWindow).GetMethod("_NavigateAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            await (Task)navigate.Invoke(window, [path, null, true])!;

            var reloadedTransform = (ScaleTransform)((Image)((Border)_Body(window)).Child!).RenderTransform!;
            Assert.Equal(1.0, reloadedTransform.ScaleX);
        });
    }

    // A code preview has no Image, so Ctrl+scroll must no-op instead of throwing on a null RenderTransform.
    [Fact]
    public async Task CtrlScrollZoom_IsANoOpOnACodePreview()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var path = Path.Combine(_dir, "Foo.cs");
            await File.WriteAllTextAsync(path, "line one");

            var window = FilePreviewWindow.Build(path, null);
            await Task.Delay(200);

            var applyZoom = typeof(FilePreviewWindow).GetMethod("_ApplyZoom", BindingFlags.NonPublic | BindingFlags.Instance)!;
            applyZoom.Invoke(window, [4.0]); // must not throw
            Assert.IsType<Grid>(_Body(window));
        });
    }

    [Fact]
    public void Window_IsResizableWithAFloorNotSizedToContent()
    {
        HeadlessAvalonia.Run(() =>
        {
            var window = new FilePreviewWindow();
            Assert.True(window.CanResize);
            Assert.Equal(SizeToContent.Manual, window.SizeToContent);
            Assert.True(window.MinWidth > 0);
            Assert.True(window.MinHeight > 0);
        });
    }

    // A hand-built single-page PDF — offsets are computed from the actual object text rather than hardcoded, so
    // a wording tweak above can't silently produce a byte-wrong xref table.
    private static byte[] _MinimalOnePagePdf()
    {
        string[] objects =
        [
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> >>\nendobj\n",
        ];

        var body = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        foreach (var obj in objects)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(body.ToString()));
            body.Append(obj);
        }

        var xrefOffset = Encoding.ASCII.GetByteCount(body.ToString());
        body.Append("xref\n").Append($"0 {objects.Length + 1}\n").Append("0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            body.Append($"{offset:D10} 00000 n \n");
        }

        body.Append("trailer\n").Append($"<< /Size {objects.Length + 1} /Root 1 0 R >>\n")
            .Append("startxref\n").Append(xrefOffset).Append('\n').Append("%%EOF");

        return Encoding.ASCII.GetBytes(body.ToString());
    }

    // A 1x1 transparent PNG — the smallest valid file `Bitmap` will decode.
    private static byte[] _TinyPng() =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
        0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
        0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
        0x42, 0x60, 0x82,
    ];
}

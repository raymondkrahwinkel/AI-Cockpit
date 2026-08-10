using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The window a clickable path opens (AC-642), exercised through the same <c>Build</c> seam
/// <c>ScreenshotPreviewWindow</c> uses — the harness's own step, not a real mouse click through
/// <c>MarkdownView</c>. One case per soort from criterion 13, plus resize (criterion 7).
/// </summary>
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

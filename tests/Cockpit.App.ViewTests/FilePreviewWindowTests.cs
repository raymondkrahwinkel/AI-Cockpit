using Avalonia.Controls;
using Avalonia.LogicalTree;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

// The window a clickable path opens (AC-642), exercised through the same `Build` seam `ScreenshotPreviewWindow`
// uses — the harness's own step, not a real mouse click through `MarkdownView`. One case per soort from
// criterion 13.
[Collection("avalonia")]
public sealed class FilePreviewWindowTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cockpit-preview-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string _KindText(Window window) =>
        window.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Name == "KindText").Text ?? string.Empty;

    private static Control _Body(Window window) =>
        (Control)window.GetLogicalDescendants().OfType<ContentControl>().First(c => c.Name == "BodyHost").Content!;

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
            await window.WaitForIdleAsync();

            Assert.Equal("pdf", _KindText(window));
            var meta = window.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Name == "MetaText").Text ?? string.Empty;
            Assert.Contains("kon niet worden geopend", meta, StringComparison.Ordinal);
            var text = Assert.IsType<TextBlock>(_Body(window));
            Assert.Equal("Geen voorbeeld voor dit bestandstype — gebruik Openen hieronder.", text.Text);
        });
    }
}

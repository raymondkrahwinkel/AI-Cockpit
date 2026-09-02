using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Sessions;
using Material.Icons;
using Material.Icons.Avalonia;

namespace Cockpit.App.ViewTests;

// AC-778: the "[+N image]" fragment on a user row is a clickable chip when the row's own bytes are still in
// memory, and no chip at all otherwise — the row's built-in "not available" state (there is no transcript
// persistence to fall back to, see the ticket's own research).
[Collection("avalonia")]
public class TranscriptUserRowImageChipViewTests
{
    // Both rows in one pane rather than one per test: the chip's Grid is built for every user row and only its
    // visibility differs, so asking which of the two carries the visible one is the whole behaviour — and it is
    // a question a per-row test asked of a single row cannot even pose.
    [Fact]
    public void OfTwoUserRows_OnlyTheOneStillHoldingItsBytes_ShowsTheChip() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();
        session.Transcript.Clear();
        var withImages = new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "look at this")
        {
            Images = [new ImageAttachment("image/png", _TinyPngBase64())],
        };
        session.Transcript.Add(withImages);
        session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "just text"));

        var window = new Window { Width = 800, Height = 600, Content = new SessionView { DataContext = session } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var visible = window.GetVisualDescendants().OfType<Button>()
                .Where(button => button.GetVisualDescendants().OfType<MaterialIcon>()
                    .Any(icon => icon.Kind == MaterialIconKind.ImageMultipleOutline))
                .Where(button => button.IsEffectivelyVisible)
                .ToList();

            var chip = Assert.Single(visible);
            Assert.Same(withImages, chip.DataContext);
        }
        finally
        {
            window.Close();
        }
    });

    // 1x1 transparent PNG — just enough for `Bitmap` to decode without throwing.
    private static string _TinyPngBase64() =>
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
}

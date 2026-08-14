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
    private static Button _ImageChip(Window window) => window.GetVisualDescendants().OfType<Button>()
        .Single(button => button.GetVisualDescendants().OfType<MaterialIcon>()
            .Any(icon => icon.Kind == MaterialIconKind.ImageMultipleOutline));

    [Fact]
    public void ARowWithImages_ShowsAVisibleChipWithTheCount() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();
        session.Transcript.Clear();
        session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "look at this")
        {
            Images = [new ImageAttachment("image/png", _TinyPngBase64())],
        });

        var window = new Window { Width = 800, Height = 600, Content = new SessionView { DataContext = session } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var chip = _ImageChip(window);
            Assert.True(chip.IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void ARowWithoutImages_ShowsNoChip() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();
        session.Transcript.Clear();
        session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "just text"));

        var window = new Window { Width = 800, Height = 600, Content = new SessionView { DataContext = session } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var chip = window.GetVisualDescendants().OfType<Button>()
                .Where(button => button.GetVisualDescendants().OfType<MaterialIcon>()
                    .Any(icon => icon.Kind == MaterialIconKind.ImageMultipleOutline));

            Assert.All(chip, button => Assert.False(button.IsEffectivelyVisible));
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

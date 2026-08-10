using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Input;
using Cockpit.App.Services;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

// The click-carrying half of AC-642, alongside MarkdownBlockReuseTests: a code-span that FilePathResolver
// resolves gets the mockup's tint and opens FilePreviewWindow; one that does not resolve is left exactly as
// before.
[Collection("avalonia")]
public sealed class MarkdownViewFilePathTests : IDisposable
{
    private readonly Func<string, bool> _originalExists = FilePathResolver.Exists;

    public void Dispose() => FilePathResolver.Exists = _originalExists;

    [Fact]
    public async Task ResolvedPath_IsTintedWithAHandCursor_DeadCodeSpanIsNot()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            FilePathResolver.Exists = p => p.EndsWith("Theme.axaml", StringComparison.Ordinal);

            var view = new MarkdownView { BasePath = @"C:\repo" };
            var window = new Window { Content = view, Width = 900, Height = 200 };
            window.Show();

            view.Markdown = "See `Theme.axaml` and `CheckBox.Switch`.";
            // First pass renders both as plain code (the probe has not answered yet); the settle callback
            // forces the rebuild that makes the resolved one tinted (AC-642 valkuil 2).
            await Task.Delay(300);

            var text = Assert.IsAssignableFrom<SelectableTextBlock>(
                Assert.Single(Assert.IsType<StackPanel>(view.Content).Children));
            var runs = text.Inlines!.OfType<Run>().ToList();

            var resolvedRun = runs.First(r => r.Text == "Theme.axaml");
            var deadRun = runs.First(r => r.Text == "CheckBox.Switch");

            Assert.NotEqual(deadRun.Foreground?.ToString(), resolvedRun.Foreground?.ToString());
            Assert.NotNull(text.Cursor);

            window.Close();
        });
    }

    [Fact]
    public async Task PlainCodeSpan_NeverProbesTheResolver()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var probed = false;
            FilePathResolver.Exists = _ => { probed = true; return true; };

            var view = new MarkdownView { BasePath = @"C:\repo" };
            var window = new Window { Content = view, Width = 900, Height = 200 };
            window.Show();

            view.Markdown = "Build with `--warnaserror`.";
            await Task.Delay(200);

            Assert.False(probed); // no separator, no short extension — the vorm filter rejects it before disk
            window.Close();
        });
    }

    [Fact]
    public async Task ClickingAResolvedPath_OpensFilePreviewWindowOwnedByTheHostWindow()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var dir = Directory.CreateTempSubdirectory("cockpit-markdown-click-");
            try
            {
                var target = Path.Combine(dir.FullName, "Theme.axaml");
                await File.WriteAllTextAsync(target, "<Styles/>");
                FilePathResolver.Exists = p => string.Equals(p, target, StringComparison.Ordinal);

                var view = new MarkdownView { BasePath = dir.FullName };
                var window = new Window { Content = view, Width = 900, Height = 200 };
                window.Show();

                view.Markdown = "See `Theme.axaml` here.";
                await Task.Delay(300);

                var text = Assert.IsAssignableFrom<SelectableTextBlock>(
                    Assert.Single(Assert.IsType<StackPanel>(view.Content).Children));

                // The block has no flat `.Text` — it is built run by run (_FillInlines) — so the position a hit
                // test needs is computed the same way the production click handler computes `link.Start`: by
                // summing run lengths, not by indexing a string that does not exist.
                var concatenated = string.Concat(text.Inlines!.OfType<Run>().Select(r => r.Text));
                var runTextPosition = concatenated.IndexOf("Theme.axaml", StringComparison.Ordinal) + 2;
                var caret = text.TextLayout.HitTestTextPosition(runTextPosition);
                var point = text.TranslatePoint(caret.Center, window)
                    ?? throw new InvalidOperationException("the run must be laid out inside the window to be clicked");

                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                await Task.Delay(300);

                var opened = Assert.Single(window.OwnedWindows);
                Assert.IsType<FilePreviewWindow>(opened);
                opened.Close();
                window.Close();
            }
            finally
            {
                dir.Delete(recursive: true);
            }
        });
    }
}

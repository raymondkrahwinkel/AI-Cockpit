using Avalonia.Controls;
using Avalonia.LogicalTree;
using Cockpit.Plugins.Abstractions;
using Cockpit.TestSupport;
using NSubstitute;
using Path = System.IO.Path;

namespace Cockpit.Plugin.SessionReview.Tests;

// AC-961: the panel is opened from another plugin — the git badge in a session's header — through an intent, which
// carries the pane and its directory as strings. What this pins is that both survive the crossing: the dialog is
// keyed to the pane that was named, and the panel it builds reads the directory that was named.
[Collection("avalonia")]
public class SessionReviewPluginTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), $"cockpit-review-intent-{Guid.NewGuid():n}");

    public SessionReviewPluginTests()
    {
        Directory.CreateDirectory(_repo);
        _Git("init", "-b", "main");
        _Git("config", "user.email", "test@example.com");
        _Git("config", "user.name", "Test");
        File.WriteAllText(Path.Combine(_repo, "Alpha.cs"), "one\n");
        _Git("add", "-A");
        _Git("commit", "-m", "first");
        File.WriteAllText(Path.Combine(_repo, "Alpha.cs"), "ONE\n");
    }

    public void Dispose()
    {
        TestGitDirectory.Remove(_repo);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task TheOpenIntent_OpensTheReviewPanelForTheSessionItNames()
    {
        var host = Substitute.For<ICockpitHost>();
        Func<PluginIntent, Task<IReadOnlyDictionary<string, string>>>? handler = null;
        host.When(h => h.RegisterIntentHandler(
                SessionReviewPlugin.OpenIntentAction,
                Arg.Any<Func<PluginIntent, Task<IReadOnlyDictionary<string, string>>>>()))
            .Do(call => handler = call.Arg<Func<PluginIntent, Task<IReadOnlyDictionary<string, string>>>>());

        Func<Control>? content = null;
        host.When(h => h.ShowDialogAsync("Session review", Arg.Any<Func<Control>>(), "review.pane-9", 1100, 720))
            .Do(call => content = call.Arg<Func<Control>>());

        using var plugin = new SessionReviewPlugin();
        plugin.Initialize(host);
        Assert.NotNull(handler);

        await handler(new PluginIntent(
            "git-status",
            "session-review",
            SessionReviewPlugin.OpenIntentAction,
            new Dictionary<string, string> { ["paneId"] = "pane-9", ["workingDirectory"] = _repo }));

        Assert.NotNull(content);

        // Build on the UI thread and poll from this one — the panel reads git on a task the constructor starts.
        var window = HeadlessAvalonia.Run(() =>
        {
            var shown = new Window { Width = 1100, Height = 720, Content = content() };
            shown.Show();
            return shown;
        });

        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline && !_ShowsAlpha(window))
            {
                Thread.Sleep(50);
            }

            Assert.True(_ShowsAlpha(window), "The panel never showed the changed file of the directory the intent named.");
        }
        finally
        {
            HeadlessAvalonia.Run(window.Close);
        }
    }

    private static bool _ShowsAlpha(Window window) => HeadlessAvalonia.Run(
        () => window.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("Alpha.cs", StringComparison.Ordinal) == true));

    private void _Git(params string[] arguments)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = _repo, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(startInfo)!;
        process.WaitForExit();
    }
}

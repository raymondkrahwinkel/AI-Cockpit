using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Media;
using Cockpit.TestSupport;
using Path = System.IO.Path;

namespace Cockpit.Plugin.GitStatus.Tests;

/// <summary>
/// The session-header indicator — the one part of AC-522 Raymond called out as the real risk, since removing
/// the plugin's dialog must leave this unchanged: colour, branch, the hover tooltip's uncommitted/unpushed
/// counts, click-to-inject, and refresh after a git command. Runs against a real repository in a temp directory,
/// same reasoning as <see cref="GitWorkflowStepsTests"/> — a faked git status would not prove what git itself
/// reports.
/// </summary>
[Collection("avalonia")]
public class GitStatusHeaderControlTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), $"cockpit-header-{Guid.NewGuid():n}");

    public GitStatusHeaderControlTests()
    {
        Directory.CreateDirectory(_repo);
        _Git("init", "-b", "main");
        _Git("config", "user.email", "test@example.com");
        _Git("config", "user.name", "Test");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "hello\n");
        _Git("add", "-A");
        _Git("commit", "-m", "first");
    }

    public void Dispose()
    {
        TestGitDirectory.Remove(_repo);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ACleanRepo_ShowsTheDoneDotAndBranch()
    {
        var harness = Harness.Attach(_repo, showBranchName: true);
        harness.WaitUntilLoaded();

        Assert.True(harness.IsVisible);
        Assert.Same(harness.Brush("CockpitStatusDoneBrush"), harness.DotFill);
        Assert.Equal("main", harness.LabelText);
        Assert.True(harness.LabelIsVisible);
        Assert.Contains("clean working tree", harness.Tooltip(), StringComparison.Ordinal);

        harness.Close();
    }

    [Fact]
    public void ARepoWithUncommittedChanges_ShowsTheWaitingDot_AndTheCountOnHover()
    {
        File.WriteAllText(Path.Combine(_repo, "README.md"), "changed\n");

        var harness = Harness.Attach(_repo, showBranchName: true);
        harness.WaitUntilLoaded();

        Assert.Same(harness.Brush("CockpitStatusWaitingBrush"), harness.DotFill);
        Assert.Contains("1 uncommitted change(s)", harness.Tooltip(), StringComparison.Ordinal);

        harness.Close();
    }

    [Fact]
    public void ADirectoryThatIsNotARepository_StaysHiddenRatherThanShowingAnError()
    {
        var plain = Path.Combine(Path.GetTempPath(), $"cockpit-header-plain-{Guid.NewGuid():n}");
        Directory.CreateDirectory(plain);
        try
        {
            var harness = Harness.Attach(plain, showBranchName: true);
            // IsVisible starts false before any read happens too, so waiting on "not visible" alone would pass
            // trivially without a real read ever completing. Wait for the read cycle itself to land instead.
            harness.WaitUntilSettled(harness.HasAttemptedLoad);

            Assert.False(harness.IsVisible);

            harness.Close();
        }
        finally
        {
            Directory.Delete(plain, recursive: true);
        }
    }

    [Fact]
    public void ShowBranchNameOff_HidesTheLabel_ButKeepsTheDotAndTheBranchOnHover()
    {
        var harness = Harness.Attach(_repo, showBranchName: false);
        harness.WaitUntilLoaded();

        Assert.True(harness.IsVisible);
        Assert.False(harness.LabelIsVisible);
        Assert.Contains("main", harness.Tooltip(), StringComparison.Ordinal);

        harness.Close();
    }

    [Fact]
    public void Click_WithAnActiveSession_InjectsTheStatusSummaryIntoIt()
    {
        var harness = Harness.Attach(_repo, showBranchName: true);
        harness.WaitUntilLoaded();
        harness.SetHasActiveSession(true);

        harness.Click();

        var injected = harness.InjectedText;
        Assert.NotNull(injected);
        Assert.Contains("main", injected, StringComparison.Ordinal);
        Assert.Contains("clean working tree", injected, StringComparison.Ordinal);
        Assert.Null(harness.ClipboardText);

        harness.Close();
    }

    [Fact]
    public void Click_WithNoActiveSession_CopiesTheSummaryToTheClipboardInstead()
    {
        var harness = Harness.Attach(_repo, showBranchName: true);
        harness.WaitUntilLoaded();
        harness.SetHasActiveSession(false);

        harness.Click();

        Assert.NotNull(harness.ClipboardText);
        Assert.Null(harness.InjectedText);

        harness.Close();
    }

    [Fact]
    public void AGitMutatingCommandInSessionOutput_RefreshesTheStatus()
    {
        var harness = Harness.Attach(_repo, showBranchName: true);
        harness.WaitUntilLoaded();
        Assert.Same(harness.Brush("CockpitStatusDoneBrush"), harness.DotFill);

        // Mutate the tree, then tell the header a git command ran — the same signal a real session's own
        // output would carry (e.g. an agent running "git commit"). The dot must catch up on its own; nothing
        // here calls back into the control directly.
        File.WriteAllText(Path.Combine(_repo, "README.md"), "changed after a commit\n");
        harness.ProduceSessionOutput("$ git commit -am 'wip'\n[main abc1234] wip\n");

        harness.WaitUntilSettled(() => harness.DotFill == harness.Brush("CockpitStatusWaitingBrush"), TimeSpan.FromSeconds(8));

        Assert.Same(harness.Brush("CockpitStatusWaitingBrush"), harness.DotFill);

        harness.Close();
    }

    private string _Git(params string[] arguments) =>
        GitCommand.RunAsync(_repo, arguments, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// One header control under test, attached to a real (headless) window so its visual-tree lifecycle events
    /// fire. Every member dispatches its own short round trip onto Avalonia's thread through
    /// <see cref="HeadlessAvalonia.Run{T}"/> rather than the caller wrapping a whole test body in one — the
    /// debounced-reload test needs the dispatcher's own loop to actually run between polls (real wall-clock
    /// time behind a real <c>DispatcherTimer</c>), which one call spanning the whole wait would block.
    /// </summary>
    private sealed class Harness
    {
        private Harness(Window window, GitStatusHeaderControl control, FakeSessionContext session, FakeCockpitActions actions)
        {
            _window = window;
            _control = control;
            _session = session;
            _actions = actions;
        }

        private readonly Window _window;
        private readonly GitStatusHeaderControl _control;
        private readonly FakeSessionContext _session;
        private readonly FakeCockpitActions _actions;

        public static Harness Attach(string workingDirectory, bool showBranchName) => HeadlessAvalonia.Run(() =>
        {
            var settings = new GitStatusSettings(new InMemoryPluginStorage()) { ShowBranchName = showBranchName };
            var actions = new FakeCockpitActions();
            var host = new FakeCockpitHost(actions);
            var session = new FakeSessionContext(workingDirectory);
            var control = new GitStatusHeaderControl(host, session, settings);

            var window = new Window { Width = 300, Height = 60, Content = control };
            window.Show();
            window.UpdateLayout();

            return new Harness(window, control, session, actions);
        });

        public bool IsVisible => HeadlessAvalonia.Run(() => _control.IsVisible);

        public IBrush? DotFill => HeadlessAvalonia.Run(() => _Field<Ellipse>("_dot").Fill);

        public bool LabelIsVisible => HeadlessAvalonia.Run(() => _Field<TextBlock>("_label").IsVisible);

        public string LabelText => HeadlessAvalonia.Run(() => _Field<TextBlock>("_label").Text) ?? string.Empty;

        public string? InjectedText => _actions.InjectedText;

        public string? ClipboardText => _actions.ClipboardText;

        public string Tooltip() => HeadlessAvalonia.Run(() => ToolTip.GetTip(_Field<Button>("_row")) as string ?? string.Empty);

        public void SetHasActiveSession(bool value) => _actions.HasActiveSession = value;

        public void Click() => HeadlessAvalonia.Run(() => _Field<Button>("_row").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));

        public void ProduceSessionOutput(string text) => HeadlessAvalonia.Run(() => _session.ProduceOutput(text));

        public IBrush? Brush(string key) => HeadlessAvalonia.Run(() =>
            Application.Current?.TryFindResource(key, out var value) == true ? value as IBrush : null);

        /// <summary>Whether a read cycle has completed at least once (successfully or not) — see the field's own comment for why <c>IsVisible</c> alone cannot tell.</summary>
        public bool HasAttemptedLoad() => HeadlessAvalonia.Run(() =>
            (typeof(GitStatusHeaderControl).GetField("_current", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("GitStatusHeaderControl no longer has a '_current' field."))
            .GetValue(_control) is not null);

        /// <summary>Waits for the initial async git-status read (a real "git status" subprocess) to land.</summary>
        public void WaitUntilLoaded() => WaitUntilSettled(() => IsVisible);

        /// <summary>
        /// Polls <paramref name="condition"/> on this (xunit) thread, sleeping between checks rather than
        /// blocking Avalonia's own thread for the whole wait — see the class comment for why.
        /// </summary>
        public void WaitUntilSettled(Func<bool> condition, TimeSpan? timeout = null)
        {
            var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("The header control did not settle into the expected state in time.");
                }

                Thread.Sleep(25);
            }
        }

        public void Close() => HeadlessAvalonia.Run(() => _window.Close());

        private T _Field<T>(string name) where T : class =>
            typeof(GitStatusHeaderControl).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(_control) as T
                ?? throw new InvalidOperationException($"GitStatusHeaderControl no longer has a '{name}' field of type {typeof(T).Name}.");
    }
}

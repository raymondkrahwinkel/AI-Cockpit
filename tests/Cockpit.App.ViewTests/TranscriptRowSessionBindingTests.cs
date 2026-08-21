using Avalonia.Controls;
using Avalonia.Logging;
using Avalonia.Threading;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Voice;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-990: a virtualised transcript row is built before it hangs in the tree, so a row that looked its session up
/// through its host found nothing and logged a binding error per realisation — 99% of every binding error the app
/// produced, and 0.4–0.7 MB of heap apiece.
/// </summary>
[Collection("avalonia")]
public class TranscriptRowSessionBindingTests
{
    private sealed class BindingErrorSink : ILogSink
    {
        private readonly List<string> _lines = [];

        public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning;

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
            => _lines.Add(messageTemplate);

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] values)
            => _lines.Add($"{messageTemplate} :: {string.Join(" | ", values)}");

        // Only the row's own session lookups; the harness's stubbed view models produce unrelated ones.
        public IReadOnlyList<string> SessionFailures =>
            [.. _lines.Where(line => line.Contains("Session.") && line.Contains("Value is null"))];
    }

    private static SessionViewModel _TranscriptOf(int rows)
    {
        var session = new SessionViewModel();
        session.Transcript.Clear();
        for (var index = 0; index < rows; index++)
        {
            session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, $"row {index}"));
        }

        return session;
    }

    private static IReadOnlyList<string> _WhileRealising(Action body)
    {
        var sink = new BindingErrorSink();
        var previous = Logger.Sink;
        Logger.Sink = sink;
        try
        {
            body();
        }
        finally
        {
            Logger.Sink = previous;
        }

        return sink.SessionFailures;
    }

    [Fact]
    public void ASessionPaneRealisingItsTranscript_LogsNoSessionBindingErrors() => HeadlessAvalonia.Run(() =>
    {
        var session = _TranscriptOf(400);

        var failures = _WhileRealising(() =>
        {
            var window = new Window { Width = 820, Height = 640, Content = new SessionView { DataContext = session } };
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            window.Close();
        });

        Assert.True(failures.Count == 0, $"{failures.Count} session lookups failed, first: {failures.FirstOrDefault()}");
    });

    [Fact]
    public void TheAssistantChatRealisingItsTranscript_LogsNoSessionBindingErrors() => HeadlessAvalonia.Run(() =>
    {
        var host = Substitute.For<IAssistantSessionHost>();
        host.Session.Returns(_TranscriptOf(400));

        var failures = _WhileRealising(() =>
        {
            var window = new AssistantChatWindow
            {
                Width = 420,
                Height = 560,
                DataContext = new AssistantChatViewModel(
                    host,
                    Substitute.For<IAssistantSettingsStore>(),
                    Substitute.For<IVoicePlaybackQueue>()),
            };
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            window.Close();
        });

        Assert.True(failures.Count == 0, $"{failures.Count} session lookups failed, first: {failures.FirstOrDefault()}");
    });

    [Fact]
    public void ARowCarriesItsSession_BeforeAnythingHasRealisedIt()
    {
        var session = _TranscriptOf(1);

        Assert.Same(session, session.Transcript[0].Session);
    }

    [Fact]
    public void ASubAgentRow_InheritsTheSessionOfTheRowItHangsUnder()
    {
        var session = _TranscriptOf(1);
        var anchor = session.Transcript[0];

        anchor.SubAgentRows.Add(new TranscriptEntryViewModel(TranscriptEntryKind.ToolUse, "nested"));

        Assert.Same(session, anchor.SubAgentRows[0].Session);
    }
}

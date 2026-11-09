using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Sessions;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App;

internal static partial class Screenshotter
{
    // The assistant's pop-out under its chip, as the mockup's "assistant" scene; with recipients, the screenshot split's
    // recipient menu instead, as its "shotmenu" scene.
    private static Window _CompanionAssistantScene(int width, int height, bool questions = false, bool recipients = false, bool tty = false)
    {
        var session = new SessionViewModel { Title = "Assistant", Capabilities = SessionCapabilities.ClaudeCli };
        session.Transcript.Clear();
        session.QueuedMessages.Clear();
        session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText,
            "Morning. The nightly run finished; two sessions still need a look."));
        if (questions)
        {
            session.Threads = new ThreadLookup(_ => "Cockpit", _ => null, _ => null);
            session.Transcript.Add(_Question("Should the screenshot go to the assistant or the selected agent?",
                ["Assistant", "Selected agent"], "cockpit"));
        }

        var chat = _AssistantChatViewModel(session);
        if (questions)
        {
            chat.OpenQuestionsCommand.Execute(null);
        }

        var feed = _SceneFeed();
        var cockpit = feed.Cockpit;
        if (tty)
        {
            var terminal = new TtyViewModel { Title = "Personal terminal" };
            cockpit.Sessions.Add(terminal);
            cockpit.SelectedSession = terminal;
        }

        var strip = new CompanionWindow(null);
        strip.SetTools(_CompanionSceneItems(feed, questions: questions ? 1 : 0));
        if (recipients)
        {
            return _CompanionDeskScene(width, height, new CompanionDesk(strip, Extra: (canvas, _) =>
                canvas.GetVisualDescendants().OfType<CompanionScreenshotView>().Single().OpenRecipients()));
        }

        var body = new CompanionAssistantChatView(chat, () => { }, () => { });

        return _CompanionDeskScene(width, height, new CompanionDesk(strip, card => _StripItem(card, "cockpit.assistant"), body, 360));
    }

    private sealed class CompanionSceneStorage : IPluginStorage
    {
        private readonly Dictionary<string, object?> _values = [];

        public T? Get<T>(string key) => _values.GetValueOrDefault(key) is T value ? value : default;

        public void Set<T>(string key, T value) => _values[key] = value;
    }
}

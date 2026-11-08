using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Layout;
using Cockpit.Core.Sessions;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App;

internal static partial class Screenshotter
{
    private static Window _CompanionAssistantScene(int width, int height, bool questions = false, bool recipients = false, bool tty = false)
    {
        var session = new SessionViewModel { Title = "Assistant", Capabilities = SessionCapabilities.ClaudeCli };
        session.Transcript.Clear();
        session.QueuedMessages.Clear();
        session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "Which change needs my attention?"));
        session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText,
            "The Companion strip is ready. You can review it here and leave a comment with a screenshot."));
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

        SessionPanelViewModel selected = tty
            ? new TtyViewModel { Title = "Personal terminal" }
            : new SessionViewModel { Title = "Review Companion strip", Capabilities = SessionCapabilities.ClaudeCli };
        var storage = new CompanionSceneStorage();
        var cockpit = new CockpitViewModel { SelectedSession = selected };
        var split = new CompanionScreenshotView(storage, cockpit, () => Task.CompletedTask, () => Task.CompletedTask);
        var tools = _CompanionSceneItems();
        tools[0] = tools[0] with
        {
            CreateView = () => new AssistantIndicator
            {
                ShowWhenDisabled = true,
                DataContext = new AssistantIndicatorViewModel
                {
                    IsFeatureEnabled = true,
                    Activity = Core.Assistant.AssistantActivity.Ready,
                    OpenQuestionCount = questions ? 1 : 0,
                },
            },
        };
        tools[1] = tools[1] with { CreateView = () => split };
        var strip = new CompanionWindow(null);
        strip.SetTools(tools);
        strip.ApplyLayout(CompanionSceneOrientation == "v" ? CompanionOrientation.Vertical : CompanionOrientation.Horizontal, true);
        var canvas = new Canvas { Width = width, Height = height };
        canvas.Bind(Panel.BackgroundProperty, canvas.GetResourceObservable("CockpitWindowBgBrush"));
        canvas.Children.Add(strip.TakeCard());
        var body = new CompanionAssistantChatView(chat, () => { }, () => { });
        canvas.Tag = (body, recipients);
        return new Window { Width = width, Height = height, Content = canvas };
    }

    private static void _StageCompanionAssistant(Window scene)
    {
        var canvas = scene.Content as Canvas ?? throw new InvalidOperationException("The companion scene needs its desktop.");
        var strip = canvas.Children[0];
        var (body, recipients) = canvas.Tag is ValueTuple<CompanionAssistantChatView, bool> state
            ? state : throw new InvalidOperationException("The companion scene needs its content.");
        var size = _Settled(scene, strip);
        var at = new PixelPoint(((int)canvas.Width - size.Width) / 2, 40);
        Canvas.SetLeft(strip, at.X);
        Canvas.SetTop(strip, at.Y);
        scene.UpdateLayout();
        if (recipients)
        {
            var split = strip.GetVisualDescendants().OfType<CompanionScreenshotView>().Single();
            split.OpenRecipients();
            return;
        }

        var indicator = strip.GetVisualDescendants().OfType<AssistantIndicator>().Single();
        var corner = indicator.TranslatePoint(default, canvas) ?? default;
        var item = new PixelRect((int)corner.X, (int)corner.Y, (int)indicator.Bounds.Width, (int)indicator.Bounds.Height);
        var orientation = CompanionSceneOrientation == "v" ? CompanionOrientation.Vertical : CompanionOrientation.Horizontal;
        if (CompanionPopOutPlacer.Place(new PixelRect(0, 0, (int)canvas.Width, (int)canvas.Height),
            new PixelRect(at, size), orientation, item, new PixelSize(400, 362), []) is { } placement)
        {
            _Put(canvas, _Inflate(placement.Bounds, CompanionPopOutCard.ShadowMargin), new CompanionPopOutCard { Body = body });
        }
    }

    private sealed class CompanionSceneStorage : IPluginStorage
    {
        private readonly Dictionary<string, object?> _values = [];

        public T? Get<T>(string key) => _values.GetValueOrDefault(key) is T value ? value : default;

        public void Set<T>(string key, T value) => _values[key] = value;
    }
}

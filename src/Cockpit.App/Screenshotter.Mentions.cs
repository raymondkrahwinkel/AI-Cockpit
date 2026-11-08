using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Mentions;

namespace Cockpit.App;

// The @-mention list in the windows that host it, one scene per mockup screen; the list is opened by `MentionStages`.
internal static partial class Screenshotter
{
    private static IEnumerable<KeyValuePair<string, Func<int, int, Window>>> MentionScenes =>
    [
        new("mention-home-at", (_, _) => _MentionHome()),
        new("mention-home-filter", (_, _) => _MentionHome()),
        new("mention-home-picked", (_, _) => _MentionHome()),
        new("mention-home-two", (_, _) => _MentionHome()),
        new("mention-home-cleared", (_, _) => _MentionHome()),
        new("mention-nogit", (_, _) => _MentionHome(withUnversioned: true)),
        new("mention-empty", (_, _) => _MentionHome()),
        new("mention-plugin", (_, _) => _MentionHome(withPlugin: true)),
        new("mention-panel", (_, height) => _MentionPanel(400, height)),
        new("mention-panel-min", (_, height) => _MentionPanel(280, height)),
        new("mention-many", (_, height) => _MentionPanel(400, height, many: true)),
        new("mention-session", (width, height) => new Window { Width = width, Height = height, Content = _MentionPicker() }),
    ];

    private static readonly Dictionary<string, Action<Window>> MentionStages = new(StringComparer.Ordinal)
    {
        ["mention-home-at"] = window => _TypeOnHome(window, "Look at @"),
        ["mention-home-filter"] = window => _TypeOnHome(window, "Look at @coc"),
        ["mention-home-picked"] = window => _PickOnHome(window, ("Cockpit", "")),
        ["mention-home-two"] = window => _PickOnHome(window, ("Cockpit", "and compare with "), ("EVE", "")),
        ["mention-home-cleared"] = window =>
        {
            _PickOnHome(window, ("Cockpit", ""));
            ((CockpitViewModel)window.DataContext!).SimpleHome.InputText = "Look at ";
        },
        ["mention-nogit"] = window => _TypeOnHome(window, "Look at @bel"),
        ["mention-empty"] = window => _TypeOnHome(window, "Look at @zzz"),
        ["mention-plugin"] = window => _TypeOnHome(window, "Look at @"),
        ["mention-panel"] = _OpenAssistantChatMentionPicker,
        ["mention-panel-min"] = _OpenAssistantChatMentionPicker,
        ["mention-many"] = window =>
        {
            _OpenAssistantChatMentionPicker(window);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var picker = window.FindDescendantOfType<AssistantChatView>()!.DataContext is AssistantChatViewModel chat ? chat.MentionPicker : null;
            var popup = window.GetVisualDescendants().OfType<MentionPopup>().First();
            if (picker?.Matches.LastOrDefault(match => match.Item?.Key.StartsWith("shared:") == true) is { } last)
            {
                popup.FindControl<ListBox>("List")!.ScrollIntoView(last);
            }
        },
        ["mention-session"] = _OpenMentionPicker,
    };

    private static Dictionary<string, Func<int, int, Window>> _WithMentionScenes(Dictionary<string, Func<int, int, Window>> scenes)
    {
        foreach (var (name, build) in MentionScenes)
        {
            scenes.Add(name, build);
        }

        return scenes;
    }

    // Home with the sample projects; the "Cockpit" item is the catalog's own project, so choosing it sets the button.
    private static Window _MentionHome(bool withUnversioned = false, bool withPlugin = false)
    {
        CockpitViewModel? cockpit = null;
        MentionItem[] unversioned = withUnversioned
            ? [new("project:belasting", "Belasting 2026", @"Z:\Belasting 2026", new MentionTile("B"), MentionKind.Project, "Belasting 2026", @"Z:\Belasting 2026", DateTimeOffset.Now.AddDays(-9))]
            : [];
        cockpit = _SimpleStandHome(mentionSources: () => _SampleMentionSources(
            name => name == "Cockpit" ? "project:" + cockpit!.Projects.Projects[0].Id : "project:" + name, unversioned)
            .Concat(withPlugin ? new IMentionSource[] { new _SampleMentionSource(20, _ => [new MentionGroup("Suggested by plugins",
                [new MentionItem("plugin:m365:planning", "Project planning", "via Microsoft 365", new MentionTile("P"), MentionKind.Suggestion, "Project planning", "m365:planning")])]) } : []).ToArray());
        return new MainWindow { DataContext = cockpit };
    }

    private static Window _MentionPanel(int width, int height, bool many = false)
    {
        string[] names = ["Atlas", "Beacon", "Compass", "Delta", "Ember", "Fjord", "Granite", "Harbor", "Iris", "Juniper", "Keystone", "Lantern",
            "Meridian", "Nimbus", "Orchid", "Pioneer", "Quartz", "Ridge", "Summit", "Tundra", "Umbra", "Vertex"];
        var local = many
            ? names.Select((name, index) => new MentionItem("project:" + name, name, "C:/Code/" + name, new MentionTile(name[..1]), MentionKind.Project, name, "C:/Code/" + name, DateTimeOffset.Now.AddDays(-3 - index))).ToArray()
            : [];
        var shared = many
            ? names.Take(9).Select(name => new MentionItem("shared:" + name, name + " Docs", "Not on this PC yet", new MentionTile(name[..1]), MentionKind.Project, name + " Docs", "depot:" + name)).ToArray()
            : [];
        var viewModel = new AssistantChatViewModel(new _FakeAssistantSessionHost { Session = null, DefaultWorkingDirectory = "/repo" },
            new _FakeAssistantSettingsStore(speakReplies: true), new _NullVoicePlaybackQueue(),
            mentionSources: _SampleMentionSources(moreLocal: local, moreShared: shared));
        return new Window { Width = width, Height = height, Content = new AssistantChatView { DataContext = viewModel } };
    }

    private static void _TypeOnHome(Window window, string text)
    {
        var box = window.GetVisualDescendants().OfType<TextBox>().First(candidate => candidate.Name == "HomeInput");
        ((CockpitViewModel)window.DataContext!).SimpleHome.InputText = text;
        box.CaretIndex = text.Length;
        ((CockpitViewModel)window.DataContext!).SimpleHome.MentionPicker.OnTextChanged(text, box.CaretIndex);
    }

    // Accepts each project the way the keys do, then writes the text the composer would hold.
    private static void _PickOnHome(Window window, params (string Query, string Tail)[] picks)
    {
        var home = ((CockpitViewModel)window.DataContext!).SimpleHome;
        var text = "Look at ";
        foreach (var (query, tail) in picks)
        {
            home.MentionPicker.OnTextChanged(text + "@" + query, text.Length + 1 + query.Length);
            text += "@" + home.MentionPicker.Accept()!.Item.InsertText + " " + tail;
        }

        home.InputText = text;
    }
}

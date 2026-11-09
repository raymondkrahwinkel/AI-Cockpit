using System.Collections.ObjectModel;
using Avalonia.Controls;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.App.Composition;
using Cockpit.Core.Sessions;

namespace Cockpit.App;

internal static partial class Screenshotter
{
    private const string SceneQuestion = """
    {"questions":[{"question":"Merge #1886 now, or wait until #1830 is in?","multiSelect":false,
      "options":[{"label":"Wait for #1830"},{"label":"Merge now"}]}]}
    """;

    // The bell's pop-out under the bell with a question, a permission and a finished agent, as the mockup's "three"
    // scene; or a terminal row and a permission.
    private static Window _CompanionNotificationsScene(int width, int height, bool terminal)
    {
        var items = new ObservableCollection<CompanionAttentionItem>();
        if (terminal)
        {
            items.Add(new CompanionAttentionItem("tty", CompanionAttentionKind.Terminal, new TtyViewModel { Title = "atlas · terminal" }, null, "Waiting for you in its terminal"));
            items.Add(_SceneRow("Bash", """{"command":"dotnet test --filter Companion"}""", "build · cockpit", CompanionAttentionKind.Permission));
        }
        else
        {
            items.Add(_SceneRow("AskUserQuestion", SceneQuestion, "Assistant asks", CompanionAttentionKind.Question));
            items.Add(_SceneRow("Edit", """{"file_path":"CHANGELOG.md"}""", "AC-2100 note images", CompanionAttentionKind.Permission, minutesAgo: 1));
            items.Add(new CompanionAttentionItem("done", CompanionAttentionKind.Done, new SessionViewModel { Title = "AC-2187 docs sweep finished" }, null,
                "Checks are green and PR #1912 is ready.") { Since = DateTimeOffset.Now.AddMinutes(-2) });
        }

        var panel = new CompanionNotificationsPanel(items, _ => { }, _ => { });
        var strip = new CompanionWindow(null);
        var waiting = items.Count(item => item.NeedsYou);
        strip.SetTools(_CompanionSceneItems(questions: terminal ? 0 : 1, needYou: waiting));

        return _CompanionDeskScene(width, height, new CompanionDesk(strip, card => _StripItem(card, CompanionAttentionFeed.ToolId), panel, 340));
    }

    private static CompanionAttentionItem _SceneRow(string tool, string input, string title, CompanionAttentionKind kind, int minutesAgo = 0)
    {
        var session = new SessionViewModel { Title = title };
        session.Apply(new ToolUseRequested { SessionId = "s", ToolUseId = "t1", ToolName = tool, InputJson = input });
        session.Apply(new PermissionRequested { SessionId = "s", ToolUseId = "t1", ToolName = tool, InputJson = input });
        var row = session.Transcript.Single(entry => entry.ToolUseId == "t1");

        return new CompanionAttentionItem(title, kind, session, row, kind == CompanionAttentionKind.Question ? row.QuestionPrompts![0].Question : row.ToolHeader)
        {
            Since = DateTimeOffset.Now.AddMinutes(-minutesAgo),
        };
    }
}

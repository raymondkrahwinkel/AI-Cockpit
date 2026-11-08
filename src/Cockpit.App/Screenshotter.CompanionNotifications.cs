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
    {"questions":[{"question":"Which suites should I run before the push?","multiSelect":false,
      "options":[{"label":"Only the affected tests"},{"label":"The whole project"}]}]}
    """;

    // The strip's notification panel on a stand-in screen: a question, a permission and a finished agent, or a terminal row.
    private static Window _CompanionNotificationsScene(bool terminal)
    {
        var items = new ObservableCollection<CompanionAttentionItem>();
        if (terminal)
        {
            items.Add(new CompanionAttentionItem("tty", CompanionAttentionKind.Terminal, new TtyViewModel { Title = "atlas · terminal" }, null, "Waiting for you in its terminal"));
            items.Add(_SceneRow("Bash", """{"command":"dotnet test --filter Companion"}""", "build · cockpit", CompanionAttentionKind.Permission));
        }
        else
        {
            items.Add(_SceneRow("AskUserQuestion", SceneQuestion, "tests · cockpit", CompanionAttentionKind.Question));
            items.Add(_SceneRow("Bash", """{"command":"git push origin feature/AC-2205"}""", "build · cockpit", CompanionAttentionKind.Permission));
            items.Add(new CompanionAttentionItem("done", CompanionAttentionKind.Done, new SessionViewModel { Title = "review · parser" }, null, "Finished"));
        }

        var card = new CompanionPopOutCard { Body = new CompanionNotificationsPanel(items, _ => { }, _ => { }) { Margin = new Avalonia.Thickness(16) } };
        var window = new Window { Width = 400, Height = terminal ? 300 : 520, Content = card };
        window.Bind(Window.BackgroundProperty, window.GetResourceObservable("CockpitWindowBgBrush"));

        return window;
    }

    private static CompanionAttentionItem _SceneRow(string tool, string input, string title, CompanionAttentionKind kind)
    {
        var session = new SessionViewModel { Title = title };
        session.Apply(new ToolUseRequested { SessionId = "s", ToolUseId = "t1", ToolName = tool, InputJson = input });
        session.Apply(new PermissionRequested { SessionId = "s", ToolUseId = "t1", ToolName = tool, InputJson = input });
        var row = session.Transcript.Single(entry => entry.ToolUseId == "t1");

        return new CompanionAttentionItem(title, kind, session, row, kind == CompanionAttentionKind.Question ? row.QuestionPrompts![0].Question : row.ToolHeader);
    }
}

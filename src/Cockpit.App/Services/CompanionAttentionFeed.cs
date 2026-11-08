using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.App.ViewModels;
using Cockpit.App.Plugins;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.CompanionTools;
using Material.Icons;

namespace Cockpit.App.Services;

public enum CompanionAttentionKind
{
    Question,
    Permission,
    Done,
    Terminal,
}

// One row of the strip's notification panel: where it came from and what it waits for.
public sealed partial class CompanionAttentionItem : ObservableObject
{
    internal CompanionAttentionItem(string key, CompanionAttentionKind kind, SessionPanelViewModel session, TranscriptEntryViewModel? row, string text)
    {
        Key = key;
        Kind = kind;
        Session = session;
        Row = row;
        Text = text;
        if (row?.QuestionPrompts is [{ MultiSelect: false } prompt])
        {
            Question = prompt;
            Options = [.. prompt.Options.Select(option => option.Label)];
            AllowsFreeText = prompt.AllowOther || prompt.Options.Count == 0;
        }
    }

    internal string Key { get; }

    public CompanionAttentionKind Kind { get; }

    internal SessionPanelViewModel Session { get; }

    internal TranscriptEntryViewModel? Row { get; }

    internal AskUserQuestionViewModel? Question { get; }

    public string SessionTitle => Session.Title;

    public string Text { get; }

    public IReadOnlyList<string> Options { get; } = [];

    public bool AllowsFreeText { get; }

    [ObservableProperty]
    private string _reply = string.Empty;

    // Allow and Deny reach the session that raised the row, by tool-use id, never the selected session.
    internal async Task RespondAsync(bool allow)
    {
        if (Session is SessionViewModel session && Row?.ToolUseId is { } toolUseId)
        {
            await session.RespondToPermissionByIdAsync(toolUseId, allow);
        }
    }

    // The answer goes through the command of the row's own session, as the button in its transcript does.
    internal async Task AnswerAsync(string answer)
    {
        if (Session is not SessionViewModel session || Row is not { } row || Question is not { } question || answer.Trim().Length == 0)
        {
            return;
        }

        if (question.Options.FirstOrDefault(option => option.Label == answer) is { } option)
        {
            option.SelectCommand.Execute(null);
        }
        else if (question.AllowOther)
        {
            question.SelectOtherCommand.Execute(null);
            question.OtherText = answer.Trim();
        }

        await session.SubmitQuestionAnswersCommand.ExecuteAsync(row);
    }

    // A TTY pane is turn-time in its own TUI (AC-294), so only an SDK session offers answer buttons.
    public bool IsPermission => Kind == CompanionAttentionKind.Permission;

    public bool IsQuestion => Kind == CompanionAttentionKind.Question && Question is not null;

    public bool IsDone => Kind == CompanionAttentionKind.Done;

    public bool NeedsYou => Kind != CompanionAttentionKind.Done;

    // A question this strip cannot show whole (several questions, or ticks) goes to the transcript row, like a TTY row.
    public bool OpensInCockpit => Kind is CompanionAttentionKind.Terminal or CompanionAttentionKind.Done
        || Kind == CompanionAttentionKind.Question && Question is null;

    public MaterialIconKind Glyph => Kind switch
    {
        CompanionAttentionKind.Question => MaterialIconKind.HelpCircleOutline,
        CompanionAttentionKind.Done => MaterialIconKind.CheckCircleOutline,
        _ => MaterialIconKind.ShieldAlertOutline,
    };
}

// Gathers what waits for the operator across all sessions (the same rows the transcript draws), so the strip's
// bell can hold them and answer them without ever consulting the selected session.
public sealed partial class CompanionAttentionFeed : ISingletonService
{
    public const string ToolId = "cockpit.notifications";

    private readonly CockpitViewModel _cockpit;
    private readonly ICompanionToolRegistry _registry;
    private readonly HashSet<SessionPanelViewModel> _watched = [];
    private readonly Dictionary<string, DateTimeOffset> _dismissedDone = [];
    private bool _refreshQueued;

    public CompanionAttentionFeed(CockpitViewModel cockpit, ICompanionToolRegistry registry)
    {
        _cockpit = cockpit;
        _registry = registry;
    }

    public ObservableCollection<CompanionAttentionItem> Items { get; } = [];

    public CompanionToolRegistration CreateCompanionTool() =>
        new(ToolId, "Notifications", _ => new CompanionNotificationsPanel(this))
        {
            Tooltip = "Questions and permissions waiting for you",
            IconKind = MaterialIconKind.BellOutline,
        };

    public void Start()
    {
        _cockpit.Sessions.CollectionChanged += (_, _) => _QueueRefresh();
        _cockpit.DelegatedTasks.Tasks.CollectionChanged += (_, _) => _QueueRefresh();
        _cockpit.PluginStatusTexts.CollectionChanged += (_, _) => _QueueRefresh();
        _cockpit.Projects.Projects.CollectionChanged += (_, _) => _QueueRefresh();
        _cockpit.PropertyChanged += (_, _) => _QueueRefresh();
        _QueueRefresh();
    }

    public void Dismiss(CompanionAttentionItem item)
    {
        if (item.Kind == CompanionAttentionKind.Done)
        {
            _dismissedDone[item.Session.PaneId] = item.Session.LastActivityUtc;
        }

        _Refresh();
    }

    public void OpenInCockpit(CompanionAttentionItem item)
    {
        _cockpit.OpenProjectSessionCommand.Execute(item.Session);
        (Application.Current as App)?.ShowMainWindow();
        Dismiss(item);
    }

    private void _QueueRefresh()
    {
        if (_refreshQueued)
        {
            return;
        }

        _refreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshQueued = false;
            _Refresh();
        }, DispatcherPriority.Background);
    }

    private void _Refresh()
    {
        var sessions = _cockpit.AllSessions().Concat(_cockpit.AssistantRootSession is { } assistant ? [assistant] : []).Distinct().ToList();
        foreach (var session in sessions.Where(_watched.Add))
        {
            session.PropertyChanged += _OnSessionChanged;
        }

        RefreshStatus(sessions);
        var wanted = sessions.SelectMany(_ItemsOf).ToList();
        var known = Items.ToDictionary(item => item.Key);
        var fresh = wanted.Where(item => !known.ContainsKey(item.Key)).ToList();
        foreach (var gone in Items.Where(item => wanted.TrueForAll(w => w.Key != item.Key)).ToList())
        {
            Items.Remove(gone);
        }

        foreach (var item in fresh)
        {
            Items.Add(item);
        }

        var waiting = Items.Count(item => item.NeedsYou);
        if (_registry.GetBadge(ToolId) is { } badge)
        {
            badge.Tone = waiting > 0 ? SideMenuButtonBadgeTone.Accent : SideMenuButtonBadgeTone.Neutral;
            badge.Text = Items.Count == 0 ? null : Items.Count.ToString();
        }

        // Only a new question or permission asks for the pop-out; a finished agent waits quietly on the bell.
        if (fresh.Exists(item => item.NeedsYou))
        {
            _registry.CreateContext(ToolId)?.RequestPopOut();
        }
    }

    private void _OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SessionViewModel.HasPendingPermission) or nameof(SessionViewModel.OpenQuestionCount)
            or nameof(SessionPanelViewModel.SessionStatus) or nameof(SessionPanelViewModel.Title) or nameof(SessionPanelViewModel.Statusline) or nameof(SessionPanelViewModel.ProjectId))
        {
            _QueueRefresh();
        }
    }

    private IEnumerable<CompanionAttentionItem> _ItemsOf(SessionPanelViewModel session)
    {
        if (session is SessionViewModel sdk)
        {
            foreach (var row in sdk.PendingToolPermissionRows())
            {
                yield return new CompanionAttentionItem($"{session.PaneId}/{row.ToolUseId}", CompanionAttentionKind.Permission, session, row, row.ToolHeader);
            }

            foreach (var row in sdk.OpenQuestions.Where(row => row.HasQuestionPrompts))
            {
                yield return new CompanionAttentionItem($"{session.PaneId}/{row.Id}", CompanionAttentionKind.Question, session, row,
                    row.QuestionPrompts?[0].Question ?? row.Text);
            }
        }
        else if (session.SessionStatus == SessionStatus.NeedsAttention)
        {
            yield return new CompanionAttentionItem($"{session.PaneId}/terminal", CompanionAttentionKind.Terminal, session, null, "Waiting for you in its terminal");
        }

        if (session.SessionStatus == SessionStatus.Done
            && !(_dismissedDone.TryGetValue(session.PaneId, out var dismissed) && dismissed >= session.LastActivityUtc))
        {
            yield return new CompanionAttentionItem($"{session.PaneId}/done", CompanionAttentionKind.Done, session, null, "Finished");
        }
    }
}

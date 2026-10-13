using System.Collections.ObjectModel;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Toasts;

namespace Cockpit.App.ViewModels;

// AC-1528: Home in the Simple stand — its own composer, what waits for you, and the projects with their stand.
public partial class CockpitViewModel
{
    private SimpleHomeViewModel? _simpleHome;

    public SimpleHomeViewModel SimpleHome => _simpleHome ?? _CreateSimpleHome();

    // AC-1305's source, read wider: a consent the corner notification was told to ignore still waits for you.
    public ObservableCollection<SessionPanelViewModel> SimpleWaitingForYou { get; } = [];

    public bool HasSimpleWaitingForYou => SimpleWaitingForYou.Count > 0;

    // Settable so a test makes it under a temporary folder instead of the operator's real Documents.
    internal string SimpleFallbackFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Cockpit");

    private SimpleHomeViewModel _CreateSimpleHome()
    {
        _simpleHome = new SimpleHomeViewModel(this);
        Projects.RecentCards.CollectionChanged += (_, _) => _RefreshSimpleHome();
        Sessions.CollectionChanged += (_, _) => _RefreshSimpleHome();
        _RefreshSimpleHome();
        return _simpleHome;
    }

    // Send on Home: a project starts on its own settings, a folder or nothing on the default profile, and the text goes.
    internal async Task<SessionPanelViewModel?> StartSimpleConversationAsync(
        string prompt, Project? project, string? folder, IReadOnlyList<byte[]> images)
    {
        var result = project is null ? await _SimpleDefaultStartAsync(prompt, folder)
            : _projectQuickStart is not null && await _projectQuickStart.ComposeAsync(project) is { } composed
                ? composed with { SessionName = project.Name }
                : null;
        if (result is null)
        {
            // The project names no profile that still exists, so its dialog asks; the text stays on Home meanwhile.
            if (project is not null)
            {
                await _NewSessionForProjectAsync(project, job: null);
            }

            return null;
        }

        var paneId = await _LaunchSessionFromResultAsync(result);
        if (Sessions.FirstOrDefault(session => session.PaneId == paneId) is not { } started)
        {
            return null;
        }

        // A provider without vision says so in the conversation itself (`AddPastedImage`).
        foreach (var image in images)
        {
            (started as SessionViewModel)?.AddPastedImage(image);
        }

        started.SubmitPromptWhenReady(prompt);
        SimpleSelectedSession = started;
        return started;
    }

    // The New-session dialog's default (its first profile) in that profile's folder, else Documents\Cockpit.
    private async Task<NewSessionResult?> _SimpleDefaultStartAsync(string prompt, string? folder)
    {
        IReadOnlyList<SessionProfile> profiles = _sessionProfileStore is null ? [] : await _sessionProfileStore.LoadAsync();
        if (profiles.FirstOrDefault() is not { } profile)
        {
            ToastHost.Add("There is no profile to start a conversation on yet. Add one under Settings → Profiles.",
                ToastSeverity.Warning, actionLabel: null, onAction: null);
            return null;
        }

        var directory = folder ?? profile.DefaultWorkingDirectory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Directory.CreateDirectory(SimpleFallbackFolder).FullName;
        }

        var firstLine = prompt.Split('\n')[0].Trim();
        return new NewSessionResult(
            SessionKind.Sdk,
            profile,
            SessionOptionCatalog.DefaultPermissionMode,
            SessionOptionCatalog.DefaultModel,
            SessionOptionCatalog.DefaultEffort,
            firstLine.Length > 40 ? $"{firstLine[..40].TrimEnd()}…" : firstLine,
            WorkingDirectory: directory,
            SdkLaunchOptions: profile.Defaults?.OptionDefaults,
            ProjectId: await _ProjectIdForDirectoryAsync(directory))
        {
            NameIsComposed = true,
        };
    }

    // Rebuilt rather than patched, like the corner notification: small lists, moved by sessions, projects and stands.
    private void _RefreshSimpleHome()
    {
        if (_simpleHome is null)
        {
            return;
        }

        var waiting = _ConsentPanes().Where(_WaitsForYou).ToList();
        if (!SimpleWaitingForYou.SequenceEqual(waiting))
        {
            SimpleWaitingForYou.Clear();
            foreach (var session in waiting)
            {
                SimpleWaitingForYou.Add(session);
            }

            OnPropertyChanged(nameof(HasSimpleWaitingForYou));
        }

        var cards = Projects.RecentCards.Take(3).Select(card => _HomeCard(card.Project)).ToList();
        if (!_simpleHome.ProjectCards.SequenceEqual(cards))
        {
            _simpleHome.ProjectCards.Clear();
            foreach (var card in cards)
            {
                _simpleHome.ProjectCards.Add(card);
            }
        }
    }

    private static bool _WaitsForYou(SessionPanelViewModel session) =>
        session.PendingConsent is not null || session.SessionStatus == SessionStatus.NeedsAttention;

    private SimpleHomeProjectCard _HomeCard(Project project)
    {
        var running = Sessions.Where(session => session.ProjectId == project.Id).ToList();
        if (running.FirstOrDefault(_WaitsForYou) is { } waiting)
        {
            return new SimpleHomeProjectCard(project, waiting, "waiting for you", IsWaiting: true);
        }

        var working = running.Where(session => session.SessionStatus is SessionStatus.Busy or SessionStatus.WorkingBackground).ToList();
        return working.Count == 0
            ? new SimpleHomeProjectCard(project, null, null, IsWaiting: false)
            : new SimpleHomeProjectCard(project, working[0], $"{working.Count} working", IsWaiting: false);
    }
}

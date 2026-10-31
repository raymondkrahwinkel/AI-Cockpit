using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Projects;
using Cockpit.Infrastructure.BackendApi;

namespace Cockpit.App.ViewModels;

// AC-1472 (F5.6b2): Options › Projects on a server (mockup v2, tab 4): its projects, and a clone the server makes with
// its own git credentials. The URL is sent as typed; the server refuses one that carries a credential.
public sealed partial class ServerAdminViewModel
{
    public IRemoteGitHubCredentials? GitHubCredentials { get; init; }

    public bool HasGitHubCredentials => GitHubCredentials is not null;

    [ObservableProperty]
    private string _gitHubStatus = "Checking GitHub sign-in…";

    [ObservableProperty]
    private bool _gitHubCanSignIn;

    [ObservableProperty]
    private bool _gitHubCanSignOut;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGitHubSignIn))]
    private LoginFlowRowViewModel? _gitHubSignIn;

    public bool HasGitHubSignIn => GitHubSignIn is not null;

    [RelayCommand]
    private async Task RefreshGitHubAsync()
    {
        if (GitHubCredentials is not { } credentials)
        {
            return;
        }

        try
        {
            var status = await credentials.GetGitHubStatusAsync();
            GitHubStatus = status.SignedIn && status.Source == "gh" && !string.IsNullOrWhiteSpace(status.User)
                ? $"Signed in as {status.User}"
                : status.Message;
            GitHubCanSignIn = status.Available && !status.SignedIn && status.Source is not ("GH_TOKEN" or "GITHUB_TOKEN");
            GitHubCanSignOut = status.SignedIn && status.Source == "gh";
        }
        catch (Exception)
        {
            GitHubStatus = "GitHub status could not be read. Refresh to try again.";
            GitHubCanSignIn = false;
            GitHubCanSignOut = false;
        }
    }

    [RelayCommand]
    private Task SignInGitHubAsync() => _StartGitHubSignInAsync(useToken: false);

    [RelayCommand]
    private Task PasteGitHubTokenAsync() => _StartGitHubSignInAsync(useToken: true);

    private Task _StartGitHubSignInAsync(bool useToken) => _RunAsync(async () =>
    {
        await CloseGitHubSignInAsync();
        if (GitHubCredentials is not { } credentials)
        {
            return;
        }

        var flow = new LoginFlowRowViewModel(credentials.StartGitHubSignIn(useToken, CancellationToken.None), _time)
        {
            IsSecretInput = useToken,
            InputPlaceholder = "Paste a GitHub token",
            ShowCodexDeviceHint = false,
            RestartSignIn = () => _StartGitHubSignInAsync(useToken),
        };
        flow.Completed = succeeded => Dispatcher.UIThread.Post(() => RefreshGitHubCommand.Execute(null));
        GitHubSignIn = flow;
    });

    [RelayCommand]
    public async Task CloseGitHubSignInAsync()
    {
        if (GitHubSignIn is { } flow)
        {
            GitHubSignIn = null;
            flow.Completed = null;
            flow.CodeInput = "";
            await flow.DisposeAsync();
        }
    }

    [RelayCommand]
    private Task SignOutGitHubAsync() => _RunAsync(async () =>
    {
        await CloseGitHubSignInAsync();
        if (GitHubCredentials is { } credentials)
        {
            await credentials.SignOutGitHubAsync();
            await RefreshGitHubAsync();
        }
    });

    public ObservableCollection<ServerProjectRowViewModel> Projects { get; } = [];

    public bool HasProjectsPage => _serverProjects is not null;

    public string CloneLabel => $"Clone on {Server}";

    [ObservableProperty]
    private string _cloneUrl = "";

    [ObservableProperty]
    private string _cloneBranch = "main";

    [ObservableProperty]
    private string _cloneName = "";

    // The result line as the mockup draws it: "Cloned into", the folder in mono, then size and duration.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCloneResult), nameof(CloneResult))]
    private string _clonedInto = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CloneResult))]
    private string _cloneDetail = "";

    public string CloneResult => HasCloneResult ? $"Cloned into {ClonedInto}{CloneDetail}" : "";

    public bool HasCloneResult => ClonedInto.Length > 0;

    [RelayCommand]
    private Task CloneAsync() => _RunAsync(async () =>
    {
        ClonedInto = "";
        if (_serverProjects is not { } serverProjects)
        {
            return;
        }

        await _ShowCloneAsync(await serverProjects.CloneAsync(CloneUrl.Trim(), CloneBranch.Trim(), CloneName.Trim()));
    });

    // AC-1514: "From Depot", what the server's own sources share and it has not added yet, added as the desktop's
    // "Add to my projects…" does: the server clones the repository the definition names and binds the project.
    public ObservableCollection<ServerSharedProjectRowViewModel> SharedProjects { get; } = [];

    // A source that failed is one line with its reason; no source at all is the one line the page shows instead.
    public ObservableCollection<string> SharedNotices { get; } = [];

    public IReadOnlyList<string> SharedProfiles => _profiles;

    [ObservableProperty]
    private string? _sharedProfile;

    public string AddSharedLabel => $"Add on {Server}";

    [RelayCommand]
    private Task AddSharedAsync(ServerSharedProjectRowViewModel row) => _RunAsync(async () =>
    {
        ClonedInto = "";
        if (_serverProjects is not { } serverProjects)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(SharedProfile))
        {
            Status = "Choose the profile its sessions run under first.";
            StatusIsError = true;
            return;
        }

        await _ShowCloneAsync(await serverProjects.BindSharedAsync(row.Id, SharedProfile));
        await _LoadSharedAsync(serverProjects);
    });

    private async Task _ShowCloneAsync(ServerProjectClone clone)
    {
        _ShowProjects();
        if (clone.Error is { } error)
        {
            Status = $"{clone.Name} was not cloned: {error}";
            StatusIsError = true;
            return;
        }

        CloneDetail = clone.SizeBytes is { } size ? $" · {SizeText(size)} · {DurationText(clone.Duration)}" : $" · {DurationText(clone.Duration)}";
        ClonedInto = clone.Path ?? "";
        await _LoadAuditAsync(null);
    }

    private async Task _LoadSharedAsync(IServerProjects serverProjects)
    {
        var sources = await serverProjects.ListSharedAsync();
        SharedProjects.Clear();
        SharedNotices.Clear();
        if (sources.Count == 0)
        {
            SharedNotices.Add("No Depot connection on this server.");
        }

        foreach (var source in sources)
        {
            if (!source.Succeeded)
            {
                SharedNotices.Add($"{source.SourceName}: {source.Error}");
            }

            foreach (var project in source.Projects)
            {
                SharedProjects.Add(new ServerSharedProjectRowViewModel(project.Id, project.Name, project.Description, source.SourceName, project.HasRepository));
            }
        }

        SharedProfile ??= _profiles.FirstOrDefault();
    }

    [RelayCommand]
    private Task RemoveProjectAsync(ServerProjectRowViewModel row) => _RunAsync(async () =>
    {
        if (_serverProjects is { } serverProjects)
        {
            await serverProjects.RemoveProjectAsync(row.Id);
            _ShowProjects();
            await _LoadAuditAsync(null);
        }
    });

    internal static string SizeText(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{(bytes / (double)(1L << 30)).ToString("0.#", CultureInfo.InvariantCulture)} GB",
        >= 1L << 20 => $"{bytes >> 20} MB",
        _ => $"{Math.Max(1, bytes >> 10)} KB",
    };

    internal static string DurationText(TimeSpan duration) =>
        duration.TotalMinutes >= 1 ? $"{(int)duration.TotalMinutes} m {duration.Seconds} s" : $"{Math.Max(1, (int)Math.Round(duration.TotalSeconds))} s";

    private void _ShowProjects()
    {
        Projects.Clear();
        foreach (var project in _serverProjects?.Current.Settings.Projects ?? [])
        {
            Projects.Add(new ServerProjectRowViewModel(project));
        }
    }
}

// AC-1514: one project a source on the server shares, not added there yet.
public sealed record ServerSharedProjectRowViewModel(string Id, string Name, string? Description, string SourceName, bool HasRepository = true)
{
    // The server only adds a project by cloning, so a definition without a repository is shown but cannot be added.
    public string? NoRepositoryHint => HasRepository ? null : "This Depot project names no repository, so there is nothing to clone on the server.";
}

// AC-1472: one project on the server, with the folder the server reported for it.
public sealed class ServerProjectRowViewModel(Project project)
{
    public string Id => project.Id;

    public string Name => project.Name;

    public string Path => project.SourceDirectory ?? "";
}

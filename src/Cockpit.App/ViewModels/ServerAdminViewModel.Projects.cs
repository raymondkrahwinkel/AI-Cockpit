using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Projects;

namespace Cockpit.App.ViewModels;

// AC-1472 (F5.6b2): Options › Projects on a server (mockup v2, tab 4): its projects, and a clone the server makes with
// its own git credentials. The URL is sent as typed; the server refuses one that carries a credential.
public sealed partial class ServerAdminViewModel
{
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

        var clone = await serverProjects.CloneAsync(CloneUrl.Trim(), CloneBranch.Trim(), CloneName.Trim());
        _ShowProjects();
        if (clone.Error is { } error)
        {
            Status = $"{clone.Name} was not cloned: {error}";
            return;
        }

        CloneDetail = clone.SizeBytes is { } size ? $" · {SizeText(size)} · {DurationText(clone.Duration)}" : $" · {DurationText(clone.Duration)}";
        ClonedInto = clone.Path ?? "";
        await _LoadAuditAsync(null);
    });

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

// AC-1472: one project on the server, with the folder the server reported for it.
public sealed class ServerProjectRowViewModel(Project project)
{
    public string Id => project.Id;

    public string Name => project.Name;

    public string Path => project.SourceDirectory ?? "";
}

using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Abstractions.Remote;
using Cockpit.Plugins.Abstractions.Sessions;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.ViewModels;

// AC-1457 (mockup v2 tab 3): the Health tab of one connect server. It shows what the server's health route answers and
// draws nothing the server did not send: which rows a key sees is the server's decision. Built on the UI thread.
public sealed partial class ServerHealthViewModel : ObservableObject
{
    internal const string RunsSection = "workflows-runs";
    internal const string DiscordSection = "discord";
    private const string NextRun = "Next run";
    private const string Separator = " · ";

    private readonly IRemoteServer _server;
    private readonly ILogger? _logger;
    private readonly Func<string, ILoginFlow?>? _startSignIn;
    private string _dismissed = "";
    private readonly HashSet<string> _running = [];

    public ServerHealthViewModel(IRemoteServer server, Func<string, ILoginFlow?>? startSignIn = null, ILogger? logger = null)
    {
        _server = server;
        _logger = logger;
        _startSignIn = startSignIn;
        server.Health.Changed += (_, _) => Dispatcher.UIThread.Post(Rebuild);
        server.StateChanged += (_, _) => Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(IsAdmin)));
        Rebuild();
    }

    public string Server => _server.Name;

    // Raised with the workflow's name when "Open" is pressed on a run waiting for a permission.
    public event Action<string>? OpenRequested;

    public ObservableCollection<ProfileHealthRowViewModel> Profiles { get; } = [];

    public ObservableCollection<HealthFactViewModel> Facts { get; } = [];

    public ObservableCollection<ScheduledRunRowViewModel> Runs { get; } = [];

    public bool IsAdmin => string.Equals(_server.State.Key?.Capability, "admin", StringComparison.OrdinalIgnoreCase);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsClosed))]
    private bool _isOpen;

    public bool IsClosed => !IsOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBadge), nameof(BadgeText))]
    private int _alarmCount;

    public bool HasBadge => AlarmCount > 0;

    public string BadgeText => AlarmCount.ToString(CultureInfo.InvariantCulture);

    // "15 scheduled · Discord online" for the header line; a section that is not there leaves its part out.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSummary))]
    private string _summary = "";

    public bool HasSummary => Summary.Length > 0;

    [ObservableProperty]
    private bool _hasHealth;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsAlarm), nameof(CanSignInAgain), nameof(MustAskAnAdmin))]
    private string? _alarmProfile;

    [ObservableProperty]
    private string _alarmTitle = "";

    [ObservableProperty]
    private string _alarmDetail = "";

    public bool ShowsAlarm => AlarmProfile is not null && _AlarmKey() != _dismissed;

    public bool CanSignInAgain => ShowsAlarm && IsAdmin;

    public bool MustAskAnAdmin => ShowsAlarm && !IsAdmin;

    public string AskAnAdmin => "Ask an admin key to sign this profile in.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSignIn), nameof(CanSignInAgain), nameof(SignInHeading))]
    private LoginFlowRowViewModel? _signIn;

    private string? _signInProfile;

    public bool HasSignIn => SignIn is not null;

    public string SignInHeading => $"Sign in without a browser · {Server} ({_signInProfile})";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleRuns), nameof(ShowsAll))]
    private bool _needsAttentionOnly;

    public bool ShowsAll => !NeedsAttentionOnly;

    [ObservableProperty]
    private string _runsDetail = "";

    public string NeedsAttentionLabel => $"Needs attention · {Runs.Count(run => run.NeedsAttention)}";

    public IEnumerable<ScheduledRunRowViewModel> VisibleRuns => NeedsAttentionOnly ? Runs.Where(run => run.NeedsAttention) : Runs;

    public bool HasRuns => Runs.Count > 0;

    [RelayCommand]
    private void ShowAll() => NeedsAttentionOnly = false;

    [RelayCommand]
    private void ShowNeedsAttention() => NeedsAttentionOnly = true;

    [RelayCommand]
    private void Later()
    {
        _dismissed = _AlarmKey();
        OnPropertyChanged(nameof(ShowsAlarm));
        OnPropertyChanged(nameof(CanSignInAgain));
        OnPropertyChanged(nameof(MustAskAnAdmin));
    }

    [RelayCommand]
    private async Task RunNowAsync(ScheduledRunRowViewModel? row)
    {
        if (row?.ActionId is not { } actionId)
        {
            return;
        }

        row.IsRunning = true;
        _running.Add(actionId);
        var before = row.Outcome;
        try
        {
            await _server.Health.RunActionAsync(RunsSection, actionId);

            // The run starts on the server and ends a moment later; ask again until the row says something new.
            for (var attempt = 0; attempt < 5 && Runs.FirstOrDefault(run => run.Workflow == row.Workflow)?.Outcome == before; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
                await _server.Health.RefreshAsync();
            }
        }
        finally
        {
            _running.Remove(actionId);
            row.IsRunning = false;
            foreach (var current in Runs.Where(run => run.ActionId == actionId))
            {
                current.IsRunning = false;
            }
        }
    }

    [RelayCommand]
    private void Open(ScheduledRunRowViewModel? row)
    {
        if (row is not null)
        {
            OpenRequested?.Invoke(row.Workflow);
        }
    }

    [RelayCommand]
    private async Task StartSignInAsync()
    {
        if (AlarmProfile is not { } profile || !IsAdmin || _startSignIn is null)
        {
            return;
        }

        await CloseSignInAsync();
        if (_startSignIn(profile) is not { } started)
        {
            return;
        }

        _signInProfile = profile;
        var flow = new LoginFlowRowViewModel(started);
        flow.Completed = succeeded => Dispatcher.UIThread.Post(() => _ = SettleAsync(succeeded));
        SignIn = flow;
    }

    [RelayCommand]
    private async Task CloseSignInAsync()
    {
        if (SignIn is { } flow)
        {
            SignIn = null;
            try
            {
                await flow.DisposeAsync();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger?.LogWarning(exception, "Could not close the sign-in on {Server}.", Server);
            }
        }
    }

    // The server reads the sign-in again on its own poll, so ask again until the profile reads signed in or the wait is over.
    private async Task SettleAsync(bool succeeded)
    {
        if (!succeeded)
        {
            return;
        }

        for (var attempt = 0; attempt < 15; attempt++)
        {
            await _server.Health.RefreshAsync();
            if (_server.Health.Current?.Profiles.All(profile => profile.SignIn != "expired") != false)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }

    partial void OnIsOpenChanged(bool value)
    {
        _server.Health.IsWatched = value;
        if (value)
        {
            _ = _server.Health.RefreshAsync();
        }
        else
        {
            _ = CloseSignInAsync();
        }
    }

    // Brings every part in line with the server's last answer.
    internal void Rebuild()
    {
        var health = _server.Health.Current;
        HasHealth = health is not null;
        Profiles.Clear();
        Facts.Clear();
        Runs.Clear();
        if (health is null)
        {
            AlarmCount = 0;
            AlarmProfile = null;
            Summary = "";
            RunsDetail = "";
            _Changed();
            return;
        }

        var now = DateTimeOffset.Now;
        foreach (var profile in health.Profiles)
        {
            Profiles.Add(new ProfileHealthRowViewModel(profile, now));
        }

        var runs = health.Sections.FirstOrDefault(section => section.Name == RunsSection);
        var discord = health.Sections.FirstOrDefault(section => section.Name == DiscordSection);
        _FillRuns(runs, now);
        _FillFacts(health, runs, discord, now);

        var expired = health.Profiles.Where(profile => profile.SignIn == "expired").ToList();
        AlarmCount = expired.Count + health.Sections.Count(section => !section.Healthy);
        AlarmProfile = expired.FirstOrDefault()?.Label;
        if (expired.Count == 0)
        {
            _dismissed = "";
        }

        if (expired.FirstOrDefault() is { } first)
        {
            AlarmTitle = $"{first.Provider} login on {Server} expired at {HealthTime.Clock(first.ExpiredSince, now)}";
            var notRun = Runs.Where(run => run.IsNotRun).Select(run => run.Workflow).ToList();
            AlarmDetail = string.Join(" ", new[]
            {
                notRun.Count > 0 ? $"{string.Join(", ", notRun)} did not run." : null,
                first.AnnouncedAt is { } announced ? $"Discord DM sent to you at {HealthTime.Clock(announced, now)}." : null,
            }.OfType<string>());
        }

        var parts = new[]
        {
            runs?.Rows.FirstOrDefault() is { } summary ? summary.Label.Split(Separator + "next:")[0] : null,
            discord is null ? null : discord.Healthy ? "Discord online" : "Discord offline",
        };
        Summary = string.Join(Separator, parts.OfType<string>());
        _Changed();
    }

    private void _Changed()
    {
        OnPropertyChanged(nameof(ShowsAlarm));
        OnPropertyChanged(nameof(CanSignInAgain));
        OnPropertyChanged(nameof(MustAskAnAdmin));
        OnPropertyChanged(nameof(VisibleRuns));
        OnPropertyChanged(nameof(HasRuns));
        OnPropertyChanged(nameof(NeedsAttentionLabel));
    }

    // The first row of the section is its summary; after it each workflow has an outcome row (with its action) and a
    // "Next run" row, which share the text before the first separator.
    private void _FillRuns(RemoteHealthSection? section, DateTimeOffset now)
    {
        if (section is null)
        {
            RunsDetail = "";
            return;
        }

        RunsDetail = section.Rows.FirstOrDefault()?.Label ?? "";
        var rows = section.Rows.Skip(1).Select(row => (Row: row, Title: _Title(row.Label), Detail: _Detail(row.Label))).ToList();
        var outcomes = rows.Where(entry => entry.Detail != NextRun).ToList();
        // One zone for every row goes in the heading; rows in different zones each carry their own after the schedule.
        var zones = outcomes.Select(entry => entry.Row.TimeZone).Distinct().ToList();
        var sharedZone = zones.Count == 1 ? zones[0] : null;
        if (sharedZone is not null)
        {
            RunsDetail += $"{Separator}{sharedZone}";
        }

        foreach (var outcome in outcomes)
        {
            var next = rows.FirstOrDefault(entry => entry.Title == outcome.Title && entry.Detail == NextRun).Row;
            Runs.Add(new ScheduledRunRowViewModel(outcome.Title, outcome.Detail, outcome.Row, next?.At, now, sharedZone is null) { IsRunning = outcome.Row.ActionId is { } id && _running.Contains(id) });
        }
    }

    private void _FillFacts(RemoteServerHealth health, RemoteHealthSection? runs, RemoteHealthSection? discord, DateTimeOffset now)
    {
        var server = health.Server;
        Facts.Add(new HealthFactViewModel("Version", string.Join(Separator, new[]
        {
            server.Version is { Length: > 0 } version ? $"Cockpit {version}" : null,
            server.Image is { Length: > 0 } image ? $"image {image}" : null,
        }.OfType<string>())));
        if (server.StartedAt is { } started)
        {
            Facts.Add(new HealthFactViewModel("Up", $"{HealthTime.Up(now - started)}{Separator}since {HealthTime.Stamp(started, now)}"));
        }

        if (server.Address is { Length: > 0 } address)
        {
            Facts.Add(new HealthFactViewModel("Reached at", address));
        }

        Facts.Add(new HealthFactViewModel("Assistant", server.AssistantHolder is { Length: > 0 } holder ? $"held by \"{holder}\"" : "No key holds it"));
        if (discord?.Rows.FirstOrDefault() is { } row)
        {
            Facts.Add(new HealthFactViewModel("Discord", row.At is { } last ? $"{row.Label}{Separator}last message {HealthTime.Clock(last, now)}" : row.Label));
        }

        Facts.Add(new HealthFactViewModel("Connected now", string.Join(Separator, server.Keys.Select(key => $"{key.Label} ({key.Capability})"))));
        if (runs?.Rows.FirstOrDefault() is { } summary)
        {
            Facts.Add(new HealthFactViewModel("Workflows", summary.Label));
        }
    }

    private string _AlarmKey() => string.Join(",", _server.Health.Current?.Profiles.Where(profile => profile.SignIn == "expired").Select(profile => profile.Label) ?? []);

    private static string _Title(string label) => label.Split(Separator, 2)[0];

    private static string _Detail(string label) => label.Contains(Separator, StringComparison.Ordinal) ? label.Split(Separator, 2)[1] : "";
}

public sealed record HealthFactViewModel(string Name, string Value);

public sealed class ProfileHealthRowViewModel
{
    public ProfileHealthRowViewModel(RemoteProfileHealth profile, DateTimeOffset now)
    {
        Label = profile.Label;
        Provider = profile.Provider;
        LastCheck = HealthTime.Clock(profile.LastCheck, now);
        IsExpired = profile.SignIn == "expired";
        IsSignedIn = profile.SignIn == "signedIn";
        IsReachable = profile.SignIn == "reachable";
        IsUnreachable = profile.SignIn == "unreachable";
        SignIn = profile.SignIn switch
        {
            "signedIn" => profile.Credential switch
            {
                "renewingLogin" => "✓ Signed in · renews itself",
                "apiKey" => "✓ API key",
                "apiKeyFromSecret" => "✓ API key from secret",
                _ => "✓ Signed in",
            },
            "expired" => $"✕ Expired {HealthTime.Clock(profile.ExpiredSince, now)}",
            "reachable" => "✓ Reachable",
            "unreachable" => "✕ Not reachable",
            _ => "– Not checked",
        };
    }

    public string Label { get; }

    public string Provider { get; }

    public string SignIn { get; }

    public string LastCheck { get; }

    public bool IsExpired { get; }

    public bool IsSignedIn { get; }

    public bool IsReachable { get; }

    public bool IsUnreachable { get; }

    public bool IsUnchecked => !IsExpired && !IsSignedIn && !IsReachable && !IsUnreachable;
}

public sealed partial class ScheduledRunRowViewModel : ObservableObject
{
    public ScheduledRunRowViewModel(string workflow, string outcome, RemoteHealthRow row, DateTimeOffset? next, DateTimeOffset now, bool showsZone = false)
    {
        Workflow = workflow;
        Schedule = row.Schedule is null ? "–" : showsZone && row.TimeZone is not null ? $"{row.Schedule} {row.TimeZone}" : row.Schedule;
        ActionId = row.ActionId;
        IsNotRun = outcome.StartsWith("Not run", StringComparison.Ordinal);
        IsWaiting = outcome.StartsWith("Waiting for your permission", StringComparison.Ordinal);
        IsFailed = row.Failed;
        IsCaughtUp = outcome.StartsWith("Missed, caught up", StringComparison.Ordinal);
        Outcome = outcome;
        LastRun = row.At is null ? "–" : HealthTime.Stamp(row.At, now);
        Next = next is null ? "–" : HealthTime.Stamp(next, now);
    }

    public string Workflow { get; }

    public string Schedule { get; }

    public string? ActionId { get; }

    public string Outcome { get; }

    public string LastRun { get; }

    public string Next { get; }

    public bool IsNotRun { get; }

    public bool IsWaiting { get; }

    public bool IsFailed { get; }

    public bool IsCaughtUp { get; }

    public bool NeedsAttention => IsFailed || IsWaiting;

    public bool IsOk => !IsFailed && !IsWaiting && !IsCaughtUp;

    public bool CanRun => ActionId is not null && !IsWaiting && !IsRunning;

    public bool CanOpen => IsWaiting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    private bool _isRunning;
}

// AC-1457: times as the mockup writes them, in this laptop's clock: "06:41", "today 03:00", "29 Sep 06:00".
internal static class HealthTime
{
    public static string Clock(DateTimeOffset? at, DateTimeOffset now) =>
        at is { } value ? value.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) : "–";

    public static string Stamp(DateTimeOffset? at, DateTimeOffset now)
    {
        if (at is not { } value)
        {
            return "–";
        }

        var local = value.ToLocalTime();
        var clock = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        return (local.Date - now.Date).Days switch
        {
            0 => $"today {clock}",
            1 => $"tomorrow {clock}",
            -1 => $"yesterday {clock}",
            _ => local.ToString("d MMM HH:mm", CultureInfo.InvariantCulture),
        };
    }

    public static string Up(TimeSpan up) => up.TotalDays >= 1
        ? string.Create(CultureInfo.InvariantCulture, $"{(int)up.TotalDays} days {up.Hours} h")
        : string.Create(CultureInfo.InvariantCulture, $"{up.Hours} h {up.Minutes} min");
}

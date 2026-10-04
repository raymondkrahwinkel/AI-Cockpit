using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Abstractions.Remote;
using Cockpit.Core.Assistant;
using Cockpit.Core.Profiles;

namespace Cockpit.App.ViewModels;

// AC-1475: Options › Assistant on a server. Every change goes out as the fields it names and comes back as what the
// server then holds, so this page never writes back a record it read: that record holds no secrets and would wipe them.
public sealed partial class ServerAssistantViewModel(string server, IAssistantAdministration assistant, IServerProfiles profiles, TimeProvider? time = null)
    : ObservableObject
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private bool _applying;
    private bool _justSaved;
    private Func<Task>? _retry;

    public string Server { get; } = server;

    public ServerProfileEditorViewModel Editor { get; } = new();

    public ObservableCollection<ServerProfileRowViewModel> CopyChoices { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProfile), nameof(HasNoProfile), nameof(CanSwitch), nameof(SwitchHint), nameof(ProfileLabel), nameof(ProfileDetail),
        nameof(ProviderText), nameof(ProviderMissing), nameof(UnsetText), nameof(BypassTitle), nameof(BypassDetail), nameof(AvailabilityTitle),
        nameof(AvailabilityDetail), nameof(IsRunning), nameof(IsWaiting), nameof(IsOff), nameof(IsFailed), nameof(CanEdit), nameof(HasApiKey))]
    private RemoteAssistantSettings? _settings;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private string _reportedAt = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSwitch), nameof(CanEdit))]
    private bool _isReadOnly;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string _editorLabel = "";

    [ObservableProperty]
    private string _instructions = "";

    [ObservableProperty]
    private bool _replacesStandingInstruction;

    [ObservableProperty]
    private ServerProfileRowViewModel? _selectedCopyChoice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string _messageTitle = "";

    [ObservableProperty]
    private string _message = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsErrorMessage), nameof(IsWarningMessage), nameof(IsInfoMessage))]
    private ServerAssistantMessage _messageKind;

    [ObservableProperty]
    private bool _canRetry;

    [ObservableProperty]
    private bool _linksHealth;

    [ObservableProperty]
    private bool _linksPlugins;

    public bool HasMessage => MessageTitle.Length > 0;

    public bool IsErrorMessage => MessageKind == ServerAssistantMessage.Error;

    public bool IsWarningMessage => MessageKind == ServerAssistantMessage.Warning;

    public bool IsInfoMessage => MessageKind == ServerAssistantMessage.Info;

    public bool HasProfile => Settings?.Profile is not null;

    public bool HasNoProfile => Settings is { Profile: null };

    // A server switched on with an empty slot can still be switched off.
    public bool CanSwitch => (HasProfile || Settings?.IsEnabled == true) && !IsReadOnly;

    public bool CanEdit => HasProfile && !IsReadOnly && Settings?.ProviderInstalled == true;

    public bool ProviderMissing => Settings is { Profile: not null, ProviderInstalled: false };

    public string SwitchHint => HasProfile
        ? "Off until you turn it on. Takes effect without a restart."
        : "Set up a profile first — without one it has nothing to run on.";

    public string ProfileLabel => Settings?.Profile?.Label ?? "";

    public string ProfileDetail => Settings?.Profile is { } profile
        ? string.Join(" · ", new[] { profile.Provider, profile.Model, profile.PermissionMode, $"MCP: {(profile.McpServers is { } names ? string.Join(", ", names) : "every enabled server")}" }.Where(part => !string.IsNullOrEmpty(part)))
        : "";

    public string ProviderText => ProviderMissing ? $"{Settings?.Profile?.Provider} · plugin not installed on {Server}" : Settings?.Profile?.Provider ?? "";

    public bool HasApiKey => Settings?.Profile?.HasApiKey == true;

    public string UnsetText => Settings?.UnsetReason ?? "No Assistant Profile is set.";

    public string BypassTitle => Settings?.ConsentBypass switch
    {
        { All: true } => "Every source may skip consent",
        { Sources.Count: 0 } => "No source skips consent",
        { Sources.Count: 1 } => "1 source may skip consent",
        { } bypass => $"{bypass.Sources.Count} sources may skip consent",
        null => "",
    };

    public string BypassDetail => Settings?.ConsentBypass is { All: false, Sources.Count: > 0 } bypass ? $" · {string.Join(", ", bypass.Sources)}" : "";

    public bool IsRunning => Settings?.IsAvailable == true;

    public bool IsFailed => Settings is { IsEnabled: true, IsAvailable: false, IsStoodDown: false } && _justSaved;

    public bool IsWaiting => Settings is { IsEnabled: true, IsAvailable: false } && !IsFailed;

    public bool IsOff => Settings is { IsEnabled: false, IsAvailable: false };

    // The server's own reason, word for word; only "running" and a failed restart are this page's words.
    public string AvailabilityTitle => Settings switch
    {
        { IsAvailable: true } => "Running",
        { IsEnabled: true, IsStoodDown: false } when _justSaved => "Not running.",
        { IsEnabled: false } => "Switched off.",
        { UnavailableReason: { } reason } => reason,
        _ => "",
    };

    public string AvailabilityDetail => Settings switch
    {
        { IsAvailable: true, Profile: { } profile } => $"on {profile.Label}{(profile.Model is { Length: > 0 } model ? $" · {model}" : "")}.",
        { IsEnabled: true, IsStoodDown: false, UnavailableReason: var reason } when _justSaved => $"The profile is saved; the restart failed: “{reason}”.",
        { IsEnabled: false, UnavailableReason: { } reason } => reason,
        _ => "",
    };

    [RelayCommand]
    public Task LoadAsync() => _RunAsync(async () =>
    {
        _Show(await assistant.GetAsync());
        if (Settings is { Profile: null })
        {
            CopyChoices.Clear();
            foreach (var profile in await profiles.ListAsync())
            {
                CopyChoices.Add(new ServerProfileRowViewModel(profile));
            }

            SelectedCopyChoice = CopyChoices.FirstOrDefault(choice => choice.IsSignInGood) ?? CopyChoices.FirstOrDefault();
        }
    }, keepsForm: false, failed: "Not read.");

    [RelayCommand]
    private Task CopyInAsync() => _RunAsync(async () =>
    {
        if (SelectedCopyChoice is not { } choice)
        {
            return;
        }

        if (await assistant.CopyProfileFromAsync(choice.Label) is not { } copied)
        {
            _Say(ServerAssistantMessage.Error, "Not copied.", $"{Server} no longer has a profile “{choice.Label}”.");
            return;
        }

        _Show(copied);
    }, keepsForm: false);

    [RelayCommand]
    private void Edit()
    {
        _FillForm();
        IsEditing = true;
    }

    [RelayCommand]
    private void Discard()
    {
        IsEditing = false;
        _FillForm();
    }

    [RelayCommand]
    private Task SaveAsync() => _RunAsync(async () =>
    {
        var original = Settings?.Profile;
        var patch = new RemoteAssistantProfilePatch
        {
            Label = EditorLabel.Trim() == (original?.Label ?? "") ? null : EditorLabel.Trim(),
            Instructions = Instructions == (Settings?.Instructions ?? "") ? null : Instructions,
            ReplacesStandingInstruction = ReplacesStandingInstruction == Settings?.ReplacesStandingInstruction ? null : ReplacesStandingInstruction,
            Profile = Editor.Patch(),
        };
        if (patch == new RemoteAssistantProfilePatch())
        {
            _Say(ServerAssistantMessage.Info, "Nothing to save.", "No field differs from what the server holds.");
            return;
        }

        var before = Settings;
        var saved = await assistant.UpdateProfileAsync(patch);
        _justSaved = true;
        IsEditing = false;
        _Show(saved);
        _Say(ServerAssistantMessage.Info, "Saved.", _SavedText(_Sent(patch), _Fields(before).Where(field => !_Sent(patch).Contains(field.Name) && field.Value != _Fields(saved).First(other => other.Name == field.Name).Value).Select(field => field.Name).ToList()));
    }, keepsForm: true);

    [RelayCommand]
    private Task RetryAsync() => _retry?.Invoke() ?? Task.CompletedTask;

    // The links of the messages that point elsewhere: the server group's Health tab, and this dialog's Plugins page.
    public event Action? OpenHealthRequested;

    public event Action? OpenPluginsRequested;

    [RelayCommand]
    private void OpenHealth() => OpenHealthRequested?.Invoke();

    [RelayCommand]
    private void OpenPlugins() => OpenPluginsRequested?.Invoke();

    partial void OnIsEnabledChanged(bool value)
    {
        if (_applying || Settings is null || value == Settings.IsEnabled)
        {
            return;
        }

        _ = _RunAsync(async () => _Show(await assistant.SetEnabledAsync(value)), keepsForm: false);
    }

    private void _Show(RemoteAssistantSettings settings)
    {
        _applying = true;
        try
        {
            Settings = settings;
            IsEnabled = settings.IsEnabled;
            ReportedAt = $"reported by the server · {_time.GetLocalNow().ToString("HH:mm", CultureInfo.InvariantCulture)}";
            if (!IsEditing)
            {
                _FillForm();
            }

            if (ProviderMissing)
            {
                _Say(ServerAssistantMessage.Warning, "Provider plugin not installed.", "Its settings are kept as they are and cannot be edited until the plugin is back.", plugins: true);
            }
            else if (settings.Profile?.SignIn == ProfileSignInKind.Expired && !settings.IsAvailable)
            {
                _Say(ServerAssistantMessage.Warning, "Sign the profile in on the server.", "Server health › Sign in without a browser. The assistant starts by itself once it can.", health: true);
            }
        }
        finally
        {
            _applying = false;
        }
    }

    private void _FillForm()
    {
        EditorLabel = Settings?.Profile?.Label ?? "";
        Instructions = Settings?.Instructions ?? "";
        ReplacesStandingInstruction = Settings?.ReplacesStandingInstruction ?? false;
        Editor.Fill(Settings?.Profile);
    }

    private void _Say(ServerAssistantMessage kind, string title, string text, bool health = false, bool plugins = false)
    {
        MessageKind = kind;
        MessageTitle = title;
        Message = text;
        LinksHealth = health;
        LinksPlugins = plugins;
    }

    // State H: who changed a field meanwhile is not on this side (the audit names the route, not the field), so the
    // message names what was sent and what the server kept, compared with what this page last read.
    private static IReadOnlyList<(string Name, string Value)> _Fields(RemoteAssistantSettings? settings) =>
    [
        ("label", settings?.Profile?.Label ?? ""),
        ("instructions", settings?.Instructions ?? ""),
        ("instruction mode", settings?.ReplacesStandingInstruction.ToString() ?? ""),
        ("model", settings?.Profile?.Model ?? ""),
        ("permission mode", settings?.Profile?.PermissionMode ?? ""),
        ("MCP sets", settings?.Profile?.McpServers is { } names ? string.Join(",", names) : "*"),
        ("variables", string.Join(";", (settings?.Profile?.Environment ?? []).Select(variable => $"{variable.Key}={variable.Value}"))),
    ];

    private static IReadOnlyList<string> _Sent(RemoteAssistantProfilePatch patch) =>
    [
        .. patch.Label is null ? Array.Empty<string>() : ["label"],
        .. patch.Instructions is null ? Array.Empty<string>() : ["instructions"],
        .. patch.ReplacesStandingInstruction is null ? Array.Empty<string>() : ["instruction mode"],
        .. patch.Profile?.Model is null ? Array.Empty<string>() : ["model"],
        .. patch.Profile?.PermissionMode is null ? Array.Empty<string>() : ["permission mode"],
        .. patch.Profile?.McpServers is null ? Array.Empty<string>() : ["MCP sets"],
        .. patch.Profile?.Environment is null ? Array.Empty<string>() : ["variables"],
    ];

    private static string _SavedText(IReadOnlyList<string> sent, IReadOnlyList<string> kept)
    {
        var mine = $"The {_List(sent)} you set {(_IsPlural(sent) ? "are" : "is")} now on the server";
        var many = _IsPlural(kept);
        return kept.Count == 0
            ? $"{mine}."
            : $"{mine}; the {_List(kept)} {(many ? "were" : "was")} changed on the server meanwhile and {(many ? "were" : "was")} kept, because you did not touch {(many ? "them" : "it")}.";
    }

    // "instructions", "MCP sets" and "variables" are plural on their own.
    private static bool _IsPlural(IReadOnlyList<string> names) => names.Count > 1 || names.Any(name => name.EndsWith('s'));

    private static string _List(IReadOnlyList<string> names) =>
        names.Count == 1 ? names[0] : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";

    // The one error answer cannot say why, so the page says what is likely and that nothing changed. A save that got no
    // answer keeps the form as it was, to send again.
    private async Task _RunAsync(Func<Task> action, bool keepsForm, string failed = "Not saved.")
    {
        _Say(ServerAssistantMessage.Info, "", "");
        CanRetry = false;
        _justSaved = false;
        try
        {
            await action();
        }
        catch (ArgumentException exception)
        {
            _Say(ServerAssistantMessage.Error, failed, exception.Message);
        }
        catch (UnauthorizedAccessException)
        {
            IsReadOnly = true;
            IsEditing = false;
            _Say(ServerAssistantMessage.Error, $"Refused by {Server}.", "This key may have been revoked or lost its admin rights. Nothing was changed.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
        {
            _retry = () => _RunAsync(action, keepsForm, failed);
            CanRetry = true;
            _Say(ServerAssistantMessage.Error, $"{failed.TrimEnd('.')} — {Server} did not answer.",
                keepsForm ? "Your changes are still in the form. Nothing changed on the server unless it confirms." : "Nothing changed on the server unless it confirms.");
        }
        catch (Exception exception)
        {
            _Say(ServerAssistantMessage.Error, failed, exception.Message);
        }

        _applying = true;
        IsEnabled = Settings?.IsEnabled ?? false;
        _applying = false;
    }
}

// AC-1475: how the page's one message line reads.
public enum ServerAssistantMessage
{
    Info,
    Warning,
    Error,
}

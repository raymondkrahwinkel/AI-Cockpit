using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.App.Services;
using Cockpit.Core.Abstractions.Remote;
using Cockpit.Core.Assistant;
using Cockpit.Core.Profiles;

namespace Cockpit.App.ViewModels;

// AC-1475: Options › Assistant on a server. Every change comes back as what the server then holds. AC-1995: the profile is
// edited in Options › Assistant's own Assistant Profile dialog with this server behind it, saved whole with each secret
// masked, which the server puts back.
public sealed partial class ServerAssistantViewModel(string server, IAssistantAdministration assistant, IServerProfiles profiles, ServerProfileServices services, TimeProvider? time = null)
    : ObservableObject
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private bool _applying;
    private bool _justSaved;
    private Func<Task>? _retry;

    public string Server { get; } = server;

    public ObservableCollection<ServerProfileRowViewModel> CopyChoices { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProfile), nameof(HasNoProfile), nameof(CanSwitch), nameof(SwitchHint), nameof(ProfileLabel), nameof(ProfileDetail),
        nameof(ProviderMissing), nameof(UnsetText), nameof(BypassTitle), nameof(BypassDetail), nameof(AvailabilityTitle),
        nameof(AvailabilityDetail), nameof(IsRunning), nameof(IsWaiting), nameof(IsOff), nameof(IsFailed), nameof(CanEdit))]
    private RemoteAssistantSettings? _settings;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private string _reportedAt = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSwitch), nameof(CanEdit))]
    private bool _isReadOnly;

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
    }, failed: "Not read.");

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
        if (copied.IsStoodDown)
        {
            _Say(ServerAssistantMessage.Info, "Copied — takes effect when the line is released.", "The server's assistant restarts on this profile once the key that holds the line lets go.");
        }
    });

    // Set by the page, which owns the window the dialog opens over.
    public Func<AssistantProfileDialogViewModel, Task>? ShowProfileDialog { get; set; }

    [RelayCommand]
    private Task EditAsync() => _RunAsync(async () =>
    {
        if (ShowProfileDialog is not { } show)
        {
            return;
        }

        var slot = new ServerAssistantSlotStore(Server, assistant);
        await show(await CreateProfileDialogAsync(slot));
        if (slot.IsRefused)
        {
            IsReadOnly = true;
            _Say(ServerAssistantMessage.Error, $"Refused by {Server}.", ServerAssistantSlotStore.RefusedDetail);
            return;
        }

        if (slot.Saved is not { } saved)
        {
            return;
        }

        _justSaved = true;
        _Show(saved);
        if (saved.IsStoodDown)
        {
            _Say(ServerAssistantMessage.Info, "Saved — takes effect when the line is released.", "The server's assistant restarts on this profile once the key that holds the line lets go.");
        }
        else if (!HasMessage)
        {
            _Say(ServerAssistantMessage.Info, "Saved.", "The profile is now on the server, and the assistant restarted on it.");
        }
    });

    // The dialog over the server's slot, its providers, MCP servers and brains; the provider stays the server's.
    public async Task<AssistantProfileDialogViewModel> CreateProfileDialogAsync(ServerAssistantSlotStore slot)
    {
        await services.PrepareAsync();
        var dialog = new AssistantProfileDialogViewModel(
            slot, null, pluginProviderRegistry: services, mcpServerCatalog: services, tokenEstimator: services, pluginConfigViews: services, canChooseProvider: false);
        await dialog.LoadAsync();
        if (services.BrainsUnavailable is { } note)
        {
            dialog.StatusMessage = note;
        }

        return dialog;
    }

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

        SwitchCommand.Execute(value);
    }

    // The switch's round trip, run as a command so it is tracked rather than dropped.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task SwitchAsync(bool enabled) => _RunAsync(async () => _Show(await assistant.SetEnabledAsync(enabled)));

    private void _Show(RemoteAssistantSettings settings)
    {
        _applying = true;
        try
        {
            Settings = settings;
            IsEnabled = settings.IsEnabled;
            ReportedAt = $"reported by the server · {_time.GetLocalNow().ToString("HH:mm", CultureInfo.InvariantCulture)}";
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

    private void _Say(ServerAssistantMessage kind, string title, string text, bool health = false, bool plugins = false)
    {
        MessageKind = kind;
        MessageTitle = title;
        Message = text;
        LinksHealth = health;
        LinksPlugins = plugins;
    }

    // The one error answer cannot say why, so the page says what is likely and that nothing changed.
    private async Task _RunAsync(Func<Task> action, string failed = "Not saved.")
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
            _Say(ServerAssistantMessage.Error, $"Refused by {Server}.", ServerAssistantSlotStore.RefusedDetail);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
        {
            _retry = () => _RunAsync(action, failed);
            CanRetry = true;
            _Say(ServerAssistantMessage.Error, $"{failed.TrimEnd('.')} — {Server} did not answer.", "Nothing changed on the server unless it confirms.");
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

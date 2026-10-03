using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Assistant;
using Cockpit.Core.Notifications;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Notifications;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Infrastructure.Sessions;

// AC-1357: asks every provider profile whether it is still signed in. Without a frontend it says so once when a sign-in
// runs out and once when it is back, to the attention channel and the controller's inbox as CiWatcher does; a desktop
// gets only the data, as its auth-expiry bar says it already. A first poll that reads expired is shown, not alarmed.
internal sealed class ProfileLoginHealthMonitor : BackgroundService, IProfileLoginHealth, ISingletonService
{
    // Who the message is from. Not a pane: the cockpit itself noticed this.
    private const string SenderPaneId = "cockpit-login-health";

    private readonly ISessionProfileStore _profiles;
    private readonly IProfileLoginChecker _checker;
    private readonly IAttentionNotifier _notifier;
    private readonly IAgentMessageInbox _inbox;
    private readonly INotificationSettingsStore _settingsStore;
    private readonly ILogger<ProfileLoginHealthMonitor> _logger;
    private readonly bool _alarms;

    // The expiries already announced, so a recovery is only announced after one.
    private readonly HashSet<string> _alarmed = new(StringComparer.Ordinal);

    private IReadOnlyList<ProfileLoginHealth> _current = [];

    public ProfileLoginHealthMonitor(
        ISessionProfileStore profiles,
        IProfileLoginChecker checker,
        IAttentionNotifier notifier,
        IAgentMessageInbox inbox,
        INotificationSettingsStore settingsStore,
        IPresenceDetector presence,
        ILogger<ProfileLoginHealthMonitor>? logger = null)
    {
        _profiles = profiles;
        _checker = checker;
        _notifier = notifier;
        _inbox = inbox;
        _settingsStore = settingsStore;
        _logger = logger ?? NullLogger<ProfileLoginHealthMonitor>.Instance;

        // ponytail: "no frontend" read from the detector only a frontend-less backend registers. Ceiling: a desktop
        // that also wants the alarm when its operator is away; then route by presence per alarm instead.
        _alarms = presence is AwayPresenceDetector;
    }

    public IReadOnlyList<ProfileLoginHealth> Current => Volatile.Read(ref _current);

    // On the thread pool: a provider's check may run its CLI, and StartAsync is awaited on the caller's thread.
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Run(() => _RunAsync(stoppingToken), stoppingToken);

    private async Task _RunAsync(CancellationToken stoppingToken)
    {
        try
        {
            var settings = await _settingsStore.LoadAsync(stoppingToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(settings.LoginCheckInterval);
            do
            {
                await _CheckAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "The provider sign-in check stopped; sign-in health is no longer updated this run.");
        }
    }

    private async Task _CheckAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<SessionProfile> profiles;
        try
        {
            profiles = await _profiles.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not read the profiles for the sign-in check; trying again next interval.");
            return;
        }

        var previous = Current.ToDictionary(row => row.Profile, StringComparer.Ordinal);
        var next = new List<ProfileLoginHealth>();
        foreach (var profile in profiles.Where(profile => profile.ProviderConfig is PluginProviderConfig))
        {
            previous.TryGetValue(profile.Label, out var before);
            bool signedIn;
            try
            {
                signedIn = _checker.IsLoggedIn(profile);
            }
            catch (Exception exception)
            {
                _logger.LogWarning("The sign-in check for profile {Profile} failed ({Error}); its last reading stands.", profile.Label, exception.GetType().Name);
                if (before is not null)
                {
                    next.Add(before);
                }

                continue;
            }

            var now = DateTimeOffset.UtcNow;
            var row = new ProfileLoginHealth(profile.Label, signedIn, now, signedIn ? null : before?.ExpiredSince ?? now);
            next.Add(row);

            if (!_alarms)
            {
                continue;
            }

            if (before is { SignedIn: true } && !signedIn)
            {
                _alarmed.Add(profile.Label);
                await _AlarmAsync(
                    "login-expired",
                    "Cockpit sign-in expired",
                    $"The sign-in of profile '{profile.Label}' on {Environment.MachineName} has expired (noticed {now:HH:mm} UTC). Sessions with this profile cannot start until it is signed in again.",
                    cancellationToken).ConfigureAwait(false);
            }
            else if (signedIn && _alarmed.Remove(profile.Label))
            {
                await _AlarmAsync(
                    "login-restored",
                    "Cockpit sign-in restored",
                    $"Profile '{profile.Label}' on {Environment.MachineName} is signed in again.",
                    cancellationToken).ConfigureAwait(false);
            }
        }

        Volatile.Write(ref _current, next);
    }

    // Each channel on its own: a webhook that fails must not keep the message from the controller's inbox.
    private async Task _AlarmAsync(string kind, string title, string body, CancellationToken cancellationToken)
    {
        try
        {
            await _notifier.NotifyAttentionAsync(new AttentionNotification(title, body), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not deliver the {Kind} notification.", kind);
        }

        _inbox.Deliver(SenderPaneId, AssistantIdentity.ControllerInboxPaneId, kind, body);
    }
}

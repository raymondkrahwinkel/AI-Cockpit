namespace Cockpit.Plugins.Abstractions.Notifications;

/// <summary>
/// How prominent a plugin's toast is (<see cref="ICockpitHost.ShowToast"/>) — drives the host's icon/colour
/// and how long it stays. Mirrors the host's own <c>Cockpit.Core.Toasts.ToastSeverity</c> one-for-one, but
/// lives here so a plugin can express it without referencing <c>Cockpit.Core</c> — see the isolation note on
/// <see cref="ICockpitHost"/>. The host maps this by name, not by ordinal, so the two are free to diverge in
/// declaration order without silently mis-mapping.
/// </summary>
public enum PluginToastSeverity
{
    Success,

    Warning,

    Information,

    Error,
}

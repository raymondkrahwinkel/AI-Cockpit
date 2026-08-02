namespace Cockpit.Core.Toasts;

// Visual/semantic level of an in-app toast (#61) — drives the host's icon/colour and auto-dismiss timeout.
public enum ToastSeverity
{
    Success,
    Warning,
    Information,
    Error,
}

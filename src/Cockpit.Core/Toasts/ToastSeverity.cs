namespace Cockpit.Core.Toasts;

/// <summary>Visual/semantic level of an in-app toast (#61) — drives the host's icon/colour and auto-dismiss timeout.</summary>
public enum ToastSeverity
{
    Success,
    Warning,
    Information,
    Error,
}

namespace Cockpit.Core.Toasts;

// One button on a toast (AC-1549): its label, and what a click does; the toast closes after it either way.
public sealed record ToastAction(string Label, Action OnInvoke);

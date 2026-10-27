using Cockpit.Core.Toasts;

namespace Cockpit.Core.Abstractions.Toasts;

/// <summary>
/// Shows a transient in-app toast (#61) — the reusable notification surface any part of the app (or a
/// background checker like the plugin-update check, #59) can use instead of inventing its own banner.
/// Implementations marshal onto the UI thread themselves, so this can be called from any thread.
/// </summary>
public interface IToastService
{
    /// <summary>
    /// Queues a toast for display. <paramref name="actionLabel"/>/<paramref name="onAction"/> are both
    /// optional and must be supplied together to show an action button; the toast auto-dismisses after a
    /// severity-dependent timeout (longer for <see cref="ToastSeverity.Error"/>) and always has a close button.
    /// </summary>
    void Show(string message, ToastSeverity severity, string? actionLabel = null, Action? onAction = null);

    /// <summary>
    /// The same with several buttons (AC-1549), the first one primary, and optionally <paramref name="sticky"/>: it
    /// stays until a button or the close button takes it down, for a toast that asks rather than announces.
    /// </summary>
    /// <remarks>
    /// Default shows the first action only, the toast a service without this overload can give.
    /// </remarks>
    void Show(string message, ToastSeverity severity, IReadOnlyList<ToastAction> actions, bool sticky) =>
        Show(message, severity, actions.FirstOrDefault()?.Label, actions.FirstOrDefault()?.OnInvoke);

    /// <summary>
    /// The same, kept in the notification center (AC-1802) under <paramref name="source"/> (null: the app itself). A
    /// later toast with the same <paramref name="key"/> replaces the earlier item, so a repeated check stays one item.
    /// </summary>
    /// <remarks>
    /// Default drops the source and key, the toast a service without a notification center can give.
    /// </remarks>
    void Show(string message, ToastSeverity severity, IReadOnlyList<ToastAction> actions, bool sticky, string? source, string? key) =>
        Show(message, severity, actions, sticky);
}

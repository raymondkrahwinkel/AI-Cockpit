namespace Cockpit.Core.Sessions;

/// <summary>
/// One control a running session can switch mid-conversation (#45 D4) — a plugin provider's model or reasoning
/// effort, offered in the session header's live-control panel. The provider owns the vocabulary: it names the
/// control, labels it, and lists the values, so the host renders it without knowing what it means — the running
/// mirror of a launch option, and the core-side form the driver adapter maps a plugin's
/// <c>PluginSessionLaunchOption</c> onto at the plugin boundary (kept a separate type so Core needs no reference
/// to the plugin abstractions).
/// </summary>
/// <param name="Key">Identifies the control back to the driver's <c>SetLiveOptionAsync</c>.</param>
/// <param name="Label">What the operator reads next to the control (e.g. "Model", "Effort").</param>
/// <param name="Choices">The values on offer for the dropdown.</param>
/// <param name="CurrentValue">The value the session is running on, so the panel opens on it, or <see langword="null"/> when unset.</param>
public sealed record SessionLiveOption(
    string Key,
    string Label,
    IReadOnlyList<string> Choices,
    string? CurrentValue);

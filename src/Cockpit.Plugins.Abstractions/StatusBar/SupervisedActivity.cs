namespace Cockpit.Plugins.Abstractions.StatusBar;

/// <summary>
/// One long-running, agent-started background activity the operator should be able to see and stop (AC-82) — a
/// port-forward tunnel, a watch, anything that keeps running on an agent's say-so. Pure data plus a stop callback:
/// the host renders it in the status bar and owns the Kill button (an agent cannot invoke <see cref="StopAsync"/>),
/// while the plugin supplies the list and the callback.
/// </summary>
/// <param name="Id">Stable id for this activity within its source, so the host can key a panel row to it.</param>
/// <param name="Title">A short line naming the activity, e.g. "nginx-1  :8080 → 80".</param>
/// <param name="Details">Labelled facts shown in the panel, rendered verbatim — source, target, cluster.</param>
/// <param name="StopAsync">Stops the activity; invoked by the host when the operator clicks Kill, never by an agent.</param>
public sealed record SupervisedActivity(string Id, string Title, IReadOnlyList<ActivityDetail> Details, Func<Task> StopAsync);

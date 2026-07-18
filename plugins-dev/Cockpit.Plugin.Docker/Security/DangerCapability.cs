namespace Cockpit.Plugin.Docker.Security;

/// <summary>
/// A capability that reaches past ordinary container management and is off unless the operator turned it on. Each
/// maps to a flag on <see cref="Settings.DockerSettings"/>; using one always asks afresh (Dangerous, never
/// remembered) with the literal command shown.
/// </summary>
internal enum DangerCapability
{
    /// <summary>Running a command inside a container (<c>docker exec</c>), or a one-shot <c>docker run</c>.</summary>
    Exec,
}

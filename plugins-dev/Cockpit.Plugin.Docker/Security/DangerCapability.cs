namespace Cockpit.Plugin.Docker.Security;

// A capability that reaches past ordinary container management and is off unless the operator turned it on. Each
// maps to a flag on `Settings.DockerSettings`; using one always asks afresh (Dangerous, never
// remembered) with the literal command shown.
internal enum DangerCapability
{
    // Running a command inside a container (`docker exec`), or a one-shot `docker run`.
    Exec,
}

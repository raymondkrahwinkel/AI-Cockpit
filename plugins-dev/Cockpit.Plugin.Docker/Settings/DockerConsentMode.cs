namespace Cockpit.Plugin.Docker.Settings;

// The three consent modes for the Docker daemon (AC-1348), stored alongside the endpoint they were chosen for —
// see `DockerSettings.ConsentMode`.
internal enum DockerConsentMode
{
    // Every read and every change asks, exactly as before this ticket. The default.
    AlwaysAsk,

    // Reads (`Security.DockerToolAccess`) go free; every change still asks.
    ReadFree,

    // Reads and changes go free. `AllowExec` stays a hard block regardless.
    AllFree,
}

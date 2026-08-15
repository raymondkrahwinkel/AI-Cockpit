namespace Cockpit.Plugin.LocalCi.Runtime;

// How far this machine gets towards a usable Docker. Three states, not two: "installed but the engine is not
// answering" is the common one — Docker Desktop shut down — and it deserves its own answer, because what the
// operator has to do about it is nothing like what they do about a missing install.
internal enum DockerRuntimeState
{
    // No `docker` executable — nothing to talk to.
    NotInstalled,

    // The CLI is there, the engine behind it is not answering.
    EngineNotRunning,

    // The engine answered.
    Usable,
}

// The outcome of the Docker probe, plus the sentence the operator reads. Whether the engine runs Linux containers
// is a property rather than a fourth state: the states are about reaching the engine, this is about the engine
// being the right kind. A Windows-container engine is reachable and healthy — it simply cannot run the images a
// workflow job needs.
internal sealed record DockerRuntimeStatus(DockerRuntimeState State, string? ContainerOs, string? ServerVersion)
{
    private const string LinuxContainerOs = "linux";

    public static DockerRuntimeStatus NotInstalled { get; } = new(DockerRuntimeState.NotInstalled, null, null);

    public static DockerRuntimeStatus EngineNotRunning { get; } = new(DockerRuntimeState.EngineNotRunning, null, null);

    public bool RunsLinuxContainers =>
        State == DockerRuntimeState.Usable && string.Equals(ContainerOs, LinuxContainerOs, StringComparison.OrdinalIgnoreCase);

    // Everything a workflow job needs from Docker is in place.
    public bool IsReady => RunsLinuxContainers;

    public string Message => State switch
    {
        DockerRuntimeState.NotInstalled =>
            "Docker was not found. Install Docker Desktop (or the Docker engine) to run workflow jobs on this machine. " +
            "Just installed it? Try again — no need to reopen this screen or restart the cockpit.",
        DockerRuntimeState.EngineNotRunning =>
            "Docker is installed, but the engine did not answer — Docker Desktop is most likely not running. Start it, then check again.",
        _ when RunsLinuxContainers =>
            $"Docker {ServerVersion} is running with Linux containers.",
        _ when ContainerOs is null =>
            "Docker is running, but it did not say which kind of containers it runs. Workflow images are Linux; check that Docker is in Linux-container mode.",
        _ =>
            $"Docker is running in {ContainerOs}-container mode. Workflow images are Linux — switch Docker to Linux containers.",
    };
}

namespace Cockpit.Plugin.LocalCi.Execution;

/// <summary>What one local run is asked to do: a job, in a workflow, in a checkout.</summary>
/// <param name="ProjectRoot">The session's worktree — the code the run is about, not a fresh clone.</param>
/// <param name="WorkflowPath">Absolute path to the workflow file, as <c>WorkflowCatalog</c> produced it.</param>
internal sealed record LocalRunRequest(string ProjectRoot, string WorkflowPath, string JobId);

/// <summary>
/// The knobs a machine needs turned before act is any use here. Kept apart from the command builder so the
/// defaults are one readable place and a test can vary them without reciting a whole argument list.
/// </summary>
/// <param name="RunnerImage">The image act runs the job in. Named explicitly on every run — see <see cref="ActCommand"/>.</param>
/// <param name="CpuLimit">How many cores the job container may use.</param>
internal sealed record ActRunOptions(string RunnerImage, int CpuLimit)
{
    /// <summary>act's own medium image: the tools an action needs to bootstrap, without the 50 GB of the large one.</summary>
    public const string DefaultRunnerImage = "catthehacker/ubuntu:act-latest";

    /// <summary>The package cache that survives between runs — the whole reason a second run is faster than the first.</summary>
    public const string NugetVolume = "cockpit-local-ci-nuget";

    /// <summary>The SDK the setup step installs, kept out of the container's lifetime for the same reason.</summary>
    public const string DotnetVolume = "cockpit-local-ci-dotnet";

    public const string NugetMount = "/opt/cockpit-nuget";

    public const string DotnetMount = "/opt/cockpit-dotnet";

    /// <summary>Stamped on every container of a run, so stopping one can find what it left behind.</summary>
    public const string RunLabel = "cockpit-local-ci";

    /// <summary>
    /// Half the cores, never fewer than two. The machine this runs on is the machine the operator is working on,
    /// and several sessions are usually live: a container build that takes every core makes the cockpit itself
    /// unusable for the length of the run, which costs more than the minutes it saves.
    /// </summary>
    public static ActRunOptions For(int processorCount, string? runnerImage = null) =>
        new(
            string.IsNullOrWhiteSpace(runnerImage) ? DefaultRunnerImage : runnerImage.Trim(),
            Math.Max(2, processorCount / 2));
}

/// <summary>
/// Turns a request into act's argument vector. A pure function on purpose: this is where every decision about how
/// this plugin drives act lives, and it is the only part of the run that can be asserted without Docker.
/// </summary>
internal static class ActCommand
{
    public static IReadOnlyList<string> Build(LocalRunRequest request, string runnerLabel, ActRunOptions options, string runId) =>
    [
        // act's own reading of which event the workflow is for. Picking one ourselves would be a guess, and a
        // workflow that only triggers on pull_request would silently run nothing under an assumed "push".
        "--detect-event",
        "-W", RelativeWorkflowPath(request),
        "-j", request.JobId,
        "-C", request.ProjectRoot,

        // Always named, never left to act's default. Without it act asks the operator to pick an image on first
        // use — an interactive prompt on a stdin nothing is attached to, which fails the run before it starts.
        "-P", $"{runnerLabel}={options.RunnerImage}",

        // Stops act re-pulling images and actions it already has, while still fetching one it does not —
        // "--pull=false" would instead fail hard on a machine that has never run this.
        "--action-offline-mode",
        "--rm",

        // Both named rather than relying on the image's HOME: a container that runs as another user would mount
        // a cache nobody reads, which looks exactly like a warm cache that is not warming anything.
        "--env", $"NUGET_PACKAGES={ActRunOptions.NugetMount}",
        "--env", $"DOTNET_INSTALL_DIR={ActRunOptions.DotnetMount}",
        "--container-options", ContainerOptions(options, runId),
    ];

    /// <summary>
    /// The workflow path relative to the checkout, with forward slashes. act is handed the checkout as its working
    /// directory, so an absolute Windows path here would name a file that does not exist inside the container.
    /// </summary>
    public static string RelativeWorkflowPath(LocalRunRequest request) =>
        Path.GetRelativePath(request.ProjectRoot, request.WorkflowPath).Replace('\\', '/');

    public static string ContainerOptions(ActRunOptions options, string runId) =>
        $"--cpus={options.CpuLimit} " +
        $"--label {ActRunOptions.RunLabel}={runId} " +
        $"-v {ActRunOptions.NugetVolume}:{ActRunOptions.NugetMount} " +
        $"-v {ActRunOptions.DotnetVolume}:{ActRunOptions.DotnetMount}";

    /// <summary>
    /// The literal command, for the consent prompt and the log. It is what will run, not a description of it:
    /// the operator approving this is approving these exact arguments.
    /// </summary>
    public static string Describe(IReadOnlyList<string> arguments) =>
        "act " + string.Join(' ', arguments.Select(_Quote));

    private static string _Quote(string argument) => argument.Contains(' ') ? $"\"{argument}\"" : argument;
}

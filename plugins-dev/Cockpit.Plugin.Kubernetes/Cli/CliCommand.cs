namespace Cockpit.Plugin.Kubernetes.Cli;

// One helm invocation (AC-179): argv only, never a shell string, plus a locked-down environment layered onto the
// inherited one. HelmCommand decides what goes in here; CliRunner only ever sees this shape.
internal sealed record CliCommand(
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    string? StandardInput = null);

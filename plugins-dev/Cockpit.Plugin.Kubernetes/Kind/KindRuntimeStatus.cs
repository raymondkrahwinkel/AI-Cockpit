namespace Cockpit.Plugin.Kubernetes.Kind;

// Whether the `kind` binary is available, and which version (AC-179 criterion 2). The cockpit does not manage it —
// see KindRuntime's remarks for why — so the plugin says what to install instead of guessing at the first failure.
internal sealed record KindRuntimeStatus(bool IsInstalled, string? Version)
{
    public static KindRuntimeStatus NotInstalled { get; } = new(IsInstalled: false, null);

    public string Message => IsInstalled
        ? $"kind {Version} is on PATH."
        : "kind was not found on PATH. Install it with \"winget install Kubernetes.kind\" (Windows), " +
          "\"brew install kind\" (macOS), or from github.com/kubernetes-sigs/kind/releases. Just installed it? " +
          "Try again — no need to reopen this screen or restart the cockpit.";
}

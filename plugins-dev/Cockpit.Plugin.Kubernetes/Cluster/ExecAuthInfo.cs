namespace Cockpit.Plugin.Kubernetes.Cluster;

/// <summary>
/// What <see cref="KubeconfigInspector"/> found about how a context authenticates: whether it uses a kubeconfig
/// <c>exec</c> credential plugin (which runs an external OS process to mint a token — a code-execution surface) and,
/// if so, the command it would run. Surfaced to the operator at registration so an exec-auth cluster is opted into
/// knowingly, not silently.
/// </summary>
internal sealed record ExecAuthInfo(bool UsesExecAuth, string? Command);

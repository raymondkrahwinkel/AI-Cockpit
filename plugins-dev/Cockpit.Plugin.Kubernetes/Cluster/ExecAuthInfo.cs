namespace Cockpit.Plugin.Kubernetes.Cluster;

// What `KubeconfigInspector` found about how a context authenticates: whether it uses a kubeconfig
// `exec` credential plugin (which runs an external OS process to mint a token — a code-execution surface) and,
// if so, the command it would run. Surfaced to the operator at registration so an exec-auth cluster is opted into
// knowingly, not silently.
internal sealed record ExecAuthInfo(bool UsesExecAuth, string? Command);

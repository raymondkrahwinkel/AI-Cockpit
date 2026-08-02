namespace Cockpit.Plugin.Kubernetes.Security;

// The per-cluster capabilities that reach past the namespace boundary and so are off by default (AC-80): a shell
// in a pod, a tunnel into the cluster, or attaching to a running container. Each maps to a flag on
// `Model.ClusterRegistration` and is gated by `ClusterAccessGate.AuthorizeDangerAsync`.
internal enum DangerCapability
{
    Exec,
    PortForward,
    Attach,
}

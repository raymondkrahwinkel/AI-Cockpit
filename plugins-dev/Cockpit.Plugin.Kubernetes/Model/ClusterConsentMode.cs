namespace Cockpit.Plugin.Kubernetes.Model;

// The three consent modes for one registered cluster (AC-1349), stored on the registration together with the
// identity they were chosen for — see `ClusterRegistration.EffectiveConsentMode`.
public enum ClusterConsentMode
{
    // Every card asks exactly as before this ticket. The default.
    AlwaysAsk,

    // Reads (`Security.KubernetesToolAccess`) go free, in any namespace; a sensitive read or a change still asks.
    ReadFree,

    // Sensitive reads and changes go free too, but only in a namespace on `AllowedNamespaces`.
    AllFree,
}

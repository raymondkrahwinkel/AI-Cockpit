namespace Cockpit.Plugin.Kind.Settings;

// The consent mode a cluster this plugin creates is registered with in the Kubernetes plugin (AC-1349). Sent by
// name on the `cluster.register` intent, so these names match that plugin's own ClusterConsentMode.
internal enum KindConsentMode
{
    // Every card asks. The default.
    AlwaysAsk,

    // Reads skip the card; secrets, Helm values/manifests and changes still ask.
    ReadFree,

    // Changes skip it too, inside the cluster's allowed namespaces.
    AllFree,
}

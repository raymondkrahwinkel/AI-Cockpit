namespace Cockpit.Plugin.Kubernetes.Argo;

// AC-576 phase 5: the entire merge-patch argo_sync writes — deliberately just an empty sync object, so there
// is no prune or force flag here to silently pick up later. A separate constant (not built inline) so the
// "no prune, no force" claim is something a test pins, not something to trust the call site got right.
internal static class ArgoSyncOperation
{
    public const string PatchJson = """{"operation":{"sync":{}}}""";
}

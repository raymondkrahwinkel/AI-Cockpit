using Cockpit.Plugin.Kubernetes.Argo;

namespace Cockpit.Plugin.Kubernetes.Tests;

// AC-576 phase 5, review note: "no prune, no force, unless the ticket explicitly asks for them" — this pins
// the exact payload argo_sync writes, so a later change cannot silently add either flag unnoticed.
public class ArgoSyncOperationTests
{
    [Fact]
    public void PatchJson_IsExactlyAnEmptySync_NoPruneOrForce()
    {
        // The whole literal, so there is nowhere for a flag to be added unnoticed — prune and force included.
        Assert.Equal("""{"operation":{"sync":{}}}""", ArgoSyncOperation.PatchJson);
    }
}

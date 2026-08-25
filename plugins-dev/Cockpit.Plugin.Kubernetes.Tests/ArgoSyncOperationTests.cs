using Cockpit.Plugin.Kubernetes.Argo;

namespace Cockpit.Plugin.Kubernetes.Tests;

// AC-576 phase 5, review note: "no prune, no force, unless the ticket explicitly asks for them" — this pins
// the exact payload argo_sync writes, so a later change cannot silently add either flag unnoticed.
public class ArgoSyncOperationTests
{
    [Fact]
    public void PatchJson_IsExactlyAnEmptySync_NoPruneOrForce()
    {
        Assert.Equal("""{"operation":{"sync":{}}}""", ArgoSyncOperation.PatchJson);
    }

    [Fact]
    public void PatchJson_MentionsNeitherPruneNorForce()
    {
        Assert.DoesNotContain("prune", ArgoSyncOperation.PatchJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("force", ArgoSyncOperation.PatchJson, StringComparison.OrdinalIgnoreCase);
    }
}

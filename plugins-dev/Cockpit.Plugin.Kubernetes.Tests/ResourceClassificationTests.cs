using Cockpit.Plugin.Kubernetes.Mcp;

namespace Cockpit.Plugin.Kubernetes.Tests;

/// <summary>
/// The two classification helpers the jail depends on: <see cref="ApiVersionRef"/> splitting apiVersion into
/// group/version, and <see cref="ResourceScope"/> deciding a resource's real REST scope. Getting these wrong is how
/// the F1 jail-escape happened, so they are pinned directly.
/// </summary>
public class ResourceClassificationTests
{
    [Theory]
    [InlineData("v1", "", "v1")]
    [InlineData("apps/v1", "apps", "v1")]
    [InlineData("networking.k8s.io/v1", "networking.k8s.io", "v1")]
    public void ApiVersionRef_Parse_SplitsGroupAndVersion(string apiVersion, string group, string version)
    {
        var reference = ApiVersionRef.Parse(apiVersion);
        Assert.Equal(group, reference.Group);
        Assert.Equal(version, reference.Version);
    }

    [Theory]
    [InlineData("", "nodes")]
    [InlineData("", "namespaces")]
    [InlineData("", "persistentvolumes")]
    [InlineData("rbac.authorization.k8s.io", "clusterroles")]
    [InlineData("storage.k8s.io", "storageclasses")]
    public void ResourceScope_ClusterScopedKinds_AreClusterScoped(string group, string plural) =>
        Assert.True(ResourceScope.IsClusterScoped(group, plural));

    [Theory]
    [InlineData("", "pods")]
    [InlineData("", "secrets")]
    [InlineData("", "configmaps")]
    [InlineData("apps", "deployments")]
    public void ResourceScope_NamespacedKinds_AreNotClusterScoped(string group, string plural) =>
        Assert.False(ResourceScope.IsClusterScoped(group, plural));

    [Fact]
    public void ResourceScope_Secrets_AreSensitive()
    {
        Assert.True(ResourceScope.IsSensitive("", "secrets"));
        Assert.False(ResourceScope.IsSensitive("", "configmaps"));
    }

    [Fact]
    public void ResourceScope_IsCaseInsensitive() =>
        Assert.True(ResourceScope.IsClusterScoped("", "Nodes"));
}

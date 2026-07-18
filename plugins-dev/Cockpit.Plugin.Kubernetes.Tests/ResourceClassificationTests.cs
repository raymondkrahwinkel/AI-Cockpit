using Cockpit.Plugin.Kubernetes.Mcp;
using FluentAssertions;

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
        reference.Group.Should().Be(group);
        reference.Version.Should().Be(version);
    }

    [Theory]
    [InlineData("", "nodes")]
    [InlineData("", "namespaces")]
    [InlineData("", "persistentvolumes")]
    [InlineData("rbac.authorization.k8s.io", "clusterroles")]
    [InlineData("storage.k8s.io", "storageclasses")]
    public void ResourceScope_ClusterScopedKinds_AreClusterScoped(string group, string plural) =>
        ResourceScope.IsClusterScoped(group, plural).Should().BeTrue();

    [Theory]
    [InlineData("", "pods")]
    [InlineData("", "secrets")]
    [InlineData("", "configmaps")]
    [InlineData("apps", "deployments")]
    public void ResourceScope_NamespacedKinds_AreNotClusterScoped(string group, string plural) =>
        ResourceScope.IsClusterScoped(group, plural).Should().BeFalse();

    [Fact]
    public void ResourceScope_Secrets_AreSensitive()
    {
        ResourceScope.IsSensitive("", "secrets").Should().BeTrue();
        ResourceScope.IsSensitive("", "configmaps").Should().BeFalse();
    }

    [Fact]
    public void ResourceScope_IsCaseInsensitive() =>
        ResourceScope.IsClusterScoped("", "Nodes").Should().BeTrue();
}

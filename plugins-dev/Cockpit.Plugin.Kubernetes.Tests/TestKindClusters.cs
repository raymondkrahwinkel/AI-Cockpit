using Cockpit.Plugin.Kubernetes.Cli;
using Cockpit.Plugin.Kubernetes.Kind;
using Cockpit.Plugin.Kubernetes.Settings;

namespace Cockpit.Plugin.Kubernetes.Tests;

// A throwaway KindClusterManager for tests that construct KubernetesMcpTools but never exercise the kind_* tools —
// avoids repeating the same fake-runner wiring across every test file that builds KubernetesMcpTools for helm/argo
// coverage. Tests that actually need kind behaviour (KindMcpToolsTests, KindClusterManagerTests) build their own.
internal static class TestKindClusters
{
    public static KindClusterManager Unused(KubernetesSettings settings) =>
        new(settings, new CliRunner(), new KindRuntime(new CliRunner()), "kind", Directory.CreateTempSubdirectory("ac179-kind-unused").FullName);
}

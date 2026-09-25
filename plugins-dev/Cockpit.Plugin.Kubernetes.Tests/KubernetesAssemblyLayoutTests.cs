namespace Cockpit.Plugin.Kubernetes.Tests;

// AC-1394: the two-assembly layout AC-1390 pilots (GitStatusAssemblyLayoutTests), applied to this plugin — the
// backend part names no Avalonia, and the UI part never reaches into the backend.
public class KubernetesAssemblyLayoutTests
{
    [Theory]
    [InlineData(typeof(KubernetesPlugin), "Avalonia")]
    [InlineData(typeof(UI.KubernetesUi), "Cockpit.Plugin.Kubernetes")]
    public void APart_DoesNotReferenceWhatItMustNot(Type entryType, string forbidden)
    {
        var references = entryType.Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => name == forbidden || name.StartsWith(forbidden + ".", StringComparison.Ordinal));

        Assert.Empty(references);
    }
}

using System.Reflection;
namespace Cockpit.Backend.Tests;

/// <summary>
/// AC-1373: the backend suite must not reach Cockpit.App, nor Cockpit.TestSupport, which brings the Avalonia package.
/// A project reference copies its assembly into the output even before any code uses it, so the output folder tells.
/// </summary>
public class ArchitectureTests
{
    [Theory]
    [InlineData("Cockpit.App.dll")]
    [InlineData("Cockpit.TestSupport.dll")]
    public void TheBackendSuite_ShipsWithoutTheDesktopAssemblies(string assembly)
    {
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "Cockpit.Infrastructure.dll")));
        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, assembly)));
    }

    // AC-1380 acceptance 4: the seven planners moved off Avalonia's DispatcherTimer onto TimeProvider/ITimer — a
    // stray `using Avalonia.Threading;` left on one of them would put an Avalonia type back into Infrastructure's
    // own IL, which this catches even though Infrastructure ships no Avalonia package reference of its own.
    [Fact]
    public void Infrastructure_ReferencesNoAvaloniaAssembly()
    {
        var referenced = typeof(Cockpit.Infrastructure.Sessions.SessionWatcher).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(referenced, assembly => assembly.Name?.StartsWith("Avalonia", StringComparison.Ordinal) == true);
    }

    // AC-1389 (F2.1), AC-1402 (F2.14): the window half of the plugin SDK is the one project of it that names Avalonia.
    [Theory]
    [InlineData("Cockpit.Plugins.Abstractions.UI", true)]
    [InlineData("Cockpit.Plugins.Abstractions", false)]
    [InlineData("Cockpit.Core", false)]
    [InlineData("Cockpit.Infrastructure", false)]
    public void OnlyTheUiHalfOfThePluginSdk_ReferencesAvalonia(string project, bool referencesAvalonia)
    {
        var projectFile = Path.Combine(_RepositoryRoot(), "src", project, $"{project}.csproj");

        var names = System.Xml.Linq.XDocument.Load(projectFile).Descendants("PackageReference")
            .Select(reference => (string?)reference.Attribute("Include"));

        Assert.Equal((project, referencesAvalonia), (project, names.Any(name => name?.StartsWith("Avalonia", StringComparison.Ordinal) == true)));
    }

    // AC-1402 acceptance 1: the SDK assembly a backend without a window loads names no Avalonia assembly, and when one
    // creeps back in the failure names the type that carries it, not only the assembly.
    [Fact]
    public void ThePluginSdkAssembly_ReferencesNoAvaloniaAssembly()
    {
        var sdk = typeof(Cockpit.Plugins.Abstractions.ICockpitHost).Assembly;
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        var carriers = sdk.GetTypes()
            .SelectMany(type => type.GetMembers(all).Select(member => (Owner: $"{type.FullName}.{member.Name}", Types: _SignatureTypes(member))))
            .SelectMany(member => member.Types.Where(used => used.Assembly.GetName().Name?.StartsWith("Avalonia", StringComparison.Ordinal) == true)
                .Select(used => $"{member.Owner} uses {used.FullName}"));
        var assemblies = sdk.GetReferencedAssemblies()
            .Where(assembly => assembly.Name?.StartsWith("Avalonia", StringComparison.Ordinal) == true)
            .Select(assembly => $"references {assembly.Name}");

        Assert.Empty(carriers.Concat(assemblies).Distinct().Order());
    }

    private static IEnumerable<Type> _SignatureTypes(MemberInfo member) => member switch
    {
        FieldInfo field => [field.FieldType],
        PropertyInfo property => [property.PropertyType],
        EventInfo @event => @event.EventHandlerType is { } handler ? [handler] : [],
        MethodBase method => method.GetParameters().Select(parameter => parameter.ParameterType)
            .Concat(method is MethodInfo info ? [info.ReturnType] : []),
        _ => [],
    };

    private static string _RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Cockpit.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
}

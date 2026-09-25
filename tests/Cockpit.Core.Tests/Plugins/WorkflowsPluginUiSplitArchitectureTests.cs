using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;

namespace Cockpit.Core.Tests.Plugins;

// AC-1399: Workflows split into a backend part and a UI part, following GitStatus's pilot (AC-1390), with the same two
// checks YouTrackPluginUiSplitArchitectureTests makes (AC-1397): the csproj files read directly, and the compiled
// backend assembly's IL, so an Active* call in the engine — where "the active session" does not exist — turns red.
public class WorkflowsPluginUiSplitArchitectureTests
{
    // Acceptance 1: the backend names no Avalonia package, and the UI part never references the backend project.
    [Theory]
    [InlineData("Cockpit.Plugin.Workflows.csproj", "PackageReference", "Avalonia")]
    [InlineData("UI/Cockpit.Plugin.Workflows.UI.csproj", "ProjectReference", "Cockpit.Plugin.Workflows.csproj")]
    public void TheProject_NamesNothingItMustNot(string project, string element, string forbidden)
    {
        var path = Path.Combine(_RepositoryRoot(), "plugins-dev", "Cockpit.Plugin.Workflows", project);
        var includes = XDocument.Load(path).Descendants(element).Select(reference => (string?)reference.Attribute("Include"));

        Assert.DoesNotContain(includes, include => include?.Contains(forbidden, StringComparison.Ordinal) == true);
    }

    // D6: no method in the backend assembly calls a member that reads or acts on "the active session" — a flow a timer
    // fires has none; its steps name the session they mean. The failure lists every calling method by class.
    [Theory]
    [InlineData("get_ActivePaneId")]
    [InlineData("get_ActiveSessionWorkingDirectory")]
    [InlineData("get_ActiveSessionUsage")]
    [InlineData("add_ActiveSessionChanged")]
    [InlineData("add_ActiveSessionUsageChanged")]
    [InlineData("get_IsFromActiveSession")]
    [InlineData("get_HasActiveSession")]
    [InlineData("InjectIntoActiveSessionAsync")]
    [InlineData("SetActiveSessionStatusAsync")]
    public void TheBackendAssembly_CallsNoActiveSessionMember(string member)
    {
        // The newest build under bin/, whatever its configuration: built through this project's reference, the
        // plugin lands in bin/Debug even for a Release test run (the Configuration is not passed on, see AC-1392).
        var assembly = Directory
            .EnumerateFiles(Path.Combine(_RepositoryRoot(), "plugins-dev", "Cockpit.Plugin.Workflows", "bin"), "Cockpit.Plugin.Workflows.dll", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .First();

        Assert.Empty(_CallersOf(assembly, member));
    }

    // Every method whose IL calls a member reference named `member`, as "Type.Method" (nested types joined by '+').
    private static List<string> _CallersOf(string assemblyPath, string member)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var image = new PEReader(stream);
        var reader = image.GetMetadataReader();
        var tokens = reader.MemberReferences
            .Where(handle => reader.GetString(reader.GetMemberReference(handle).Name) == member)
            .Select(handle => MetadataTokens.GetToken(handle))
            .ToHashSet();

        return [.. reader.MethodDefinitions
            .Select(reader.GetMethodDefinition)
            .Where(method => method.RelativeVirtualAddress != 0 && _Calls(image.GetMethodBody(method.RelativeVirtualAddress).GetILBytes() ?? [], tokens))
            .Select(method => $"{_TypeName(reader, method.GetDeclaringType())}.{reader.GetString(method.Name)}")];
    }

    // ponytail: scans the raw IL for call/callvirt, or ldftn/ldvirtftn (a method group), followed by one of the tokens
    // rather than decoding each instruction; a false hit needs those exact bytes inside another operand, which a
    // member-reference token all but rules out. Decode instruction by instruction if one ever shows up.
    private static bool _Calls(byte[] il, HashSet<int> tokens) =>
        Enumerable.Range(0, Math.Max(0, il.Length - 4))
            .Any(index => ((il[index] == 0x28 || il[index] == 0x6F) && tokens.Contains(BitConverter.ToInt32(il, index + 1)))
                || (index + 5 < il.Length && il[index] == 0xFE && (il[index + 1] == 0x06 || il[index + 1] == 0x07)
                    && tokens.Contains(BitConverter.ToInt32(il, index + 2))));

    private static string _TypeName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(handle);
        var name = reader.GetString(type.Name);
        return type.GetDeclaringType().IsNil ? name : $"{_TypeName(reader, type.GetDeclaringType())}+{name}";
    }

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

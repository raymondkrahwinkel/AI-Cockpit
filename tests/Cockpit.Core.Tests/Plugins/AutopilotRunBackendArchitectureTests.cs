using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Cockpit.Core.Tests.Plugins;

// AC-1398: Autopilot's run backend holds its sessions by pane id, never by their view. Until AC-1418 moves the
// workspace out, both sit in one assembly, so this reads the compiled IL and signatures per type: an Avalonia type in
// a field, a signature or a call (a Control callback, session.View) turns the row red with the member named.
public class AutopilotRunBackendArchitectureTests
{
    [Theory]
    [InlineData("AutopilotRunCoordinator")]
    [InlineData("AutopilotRunContext")]
    [InlineData("AutopilotWorkspaceRuns")]
    public void TheRunBackendType_NamesNoAvaloniaType(string typeName)
    {
        // The newest build under bin/, whatever its configuration: built through this project's reference, the
        // plugin lands in bin/Debug even for a Release test run (the Configuration is not passed on, see AC-1392).
        var assembly = Directory
            .EnumerateFiles(Path.Combine(_RepositoryRoot(), "plugins-dev", "Cockpit.Plugin.Autopilot", "bin"), "Cockpit.Plugin.Autopilot.dll", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .First();

        Assert.Empty(_AvaloniaUsesIn(assembly, typeName));
    }

    // Every field, method signature and IL reference of `typeName` and the types nested in it (its lambdas and async
    // state machines) that names an Avalonia type, as "Type.Member".
    private static List<string> _AvaloniaUsesIn(string assemblyPath, string typeName)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var image = new PEReader(stream);
        var reader = image.GetMetadataReader();
        var names = new NamesAvalonia();
        // Every token an instruction can carry that leads to an Avalonia type: a member of another assembly, a generic
        // method instance, this assembly's own methods and fields (another class's Control), and the types themselves.
        var tokens = reader.MemberReferences.Where(handle => _Names(reader, names, handle)).Select(handle => MetadataTokens.GetToken(handle))
            .Concat(Enumerable.Range(1, reader.GetTableRowCount(TableIndex.MethodSpec))
                .Select(MetadataTokens.MethodSpecificationHandle)
                .Where(handle => _Names(reader, names, handle))
                .Select(handle => MetadataTokens.GetToken(handle)))
            .Concat(reader.MethodDefinitions.Where(handle => _Signature(reader.GetMethodDefinition(handle).DecodeSignature(names, null))).Select(handle => MetadataTokens.GetToken(handle)))
            .Concat(reader.FieldDefinitions.Where(handle => reader.GetFieldDefinition(handle).DecodeSignature(names, null)).Select(handle => MetadataTokens.GetToken(handle)))
            .Concat(reader.TypeReferences.Where(handle => NamesAvalonia.IsAvalonia(reader, handle)).Select(handle => MetadataTokens.GetToken(handle)))
            .Concat(Enumerable.Range(1, reader.GetTableRowCount(TableIndex.TypeSpec))
                .Select(MetadataTokens.TypeSpecificationHandle)
                .Where(handle => reader.GetTypeSpecification(handle).DecodeSignature(names, null))
                .Select(handle => MetadataTokens.GetToken(handle)))
            .ToHashSet();

        var types = reader.TypeDefinitions.Where(handle => _Outermost(reader, handle) == typeName).ToList();
        Assert.NotEmpty(types);

        return [.. types.SelectMany(handle =>
        {
            var type = reader.GetTypeDefinition(handle);
            var fields = type.GetFields().Select(reader.GetFieldDefinition)
                .Where(field => field.DecodeSignature(names, null))
                .Select(field => reader.GetString(field.Name));
            var methods = type.GetMethods().Select(reader.GetMethodDefinition)
                .Where(method => _Signature(method.DecodeSignature(names, null))
                    || (method.RelativeVirtualAddress != 0 && _Body(reader, names, image.GetMethodBody(method.RelativeVirtualAddress), tokens)))
                .Select(method => reader.GetString(method.Name));
            return fields.Concat(methods).Select(member => $"{_TypeName(reader, handle)}.{member}");
        })];
    }

    private static bool _Names(MetadataReader reader, NamesAvalonia names, MemberReferenceHandle handle)
    {
        var member = reader.GetMemberReference(handle);
        var parent = member.Parent.Kind switch
        {
            HandleKind.TypeReference => NamesAvalonia.IsAvalonia(reader, (TypeReferenceHandle)member.Parent),
            HandleKind.TypeSpecification => reader.GetTypeSpecification((TypeSpecificationHandle)member.Parent).DecodeSignature(names, null),
            _ => false,
        };

        return parent || (member.GetKind() == MemberReferenceKind.Field
            ? member.DecodeFieldSignature(names, null)
            : _Signature(member.DecodeMethodSignature(names, null)));
    }

    private static bool _Names(MetadataReader reader, NamesAvalonia names, MethodSpecificationHandle handle)
    {
        var specification = reader.GetMethodSpecification(handle);
        return specification.DecodeSignature(names, null).Contains(true)
            || (specification.Method.Kind == HandleKind.MemberReference && _Names(reader, names, (MemberReferenceHandle)specification.Method));
    }

    private static bool _Signature(MethodSignature<bool> signature) => signature.ReturnType || signature.ParameterTypes.Contains(true);

    // A body names Avalonia through a local of that type or an instruction carrying one of the tokens.
    private static bool _Body(MetadataReader reader, NamesAvalonia names, MethodBodyBlock body, HashSet<int> tokens) =>
        (!body.LocalSignature.IsNil && reader.GetStandaloneSignature(body.LocalSignature).DecodeLocalSignature(names, null).Contains(true))
        || _References(body.GetILBytes() ?? [], tokens);

    // ponytail: the raw scan YouTrackPluginUiSplitArchitectureTests uses, widened to every token-carrying opcode (calls,
    // fields, casts, box, newarr, ldtoken); a false hit needs such a token inside another operand. Decode if one shows up.
    private static bool _References(byte[] il, HashSet<int> tokens) =>
        Enumerable.Range(0, Math.Max(0, il.Length - 4))
            .Any(index => (il[index] is 0x28 or 0x6F or 0x73 or 0x74 or 0x75 or 0x79 or 0x7B or 0x7C or 0x7D or 0x7E or 0x7F or 0x80 or 0x8C or 0x8D or 0xA5 or 0xD0
                    && tokens.Contains(BitConverter.ToInt32(il, index + 1)))
                || (index + 5 < il.Length && il[index] == 0xFE && (il[index + 1] == 0x06 || il[index + 1] == 0x07)
                    && tokens.Contains(BitConverter.ToInt32(il, index + 2))));

    private static string _Outermost(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(handle);
        return type.GetDeclaringType().IsNil ? reader.GetString(type.Name) : _Outermost(reader, type.GetDeclaringType());
    }

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

    // Decodes a signature to whether it names a type from an Avalonia assembly anywhere, generic arguments included.
    private sealed class NamesAvalonia : ISignatureTypeProvider<bool, object?>
    {
        public static bool IsAvalonia(MetadataReader reader, TypeReferenceHandle handle)
        {
            var scope = reader.GetTypeReference(handle).ResolutionScope;
            return scope.Kind switch
            {
                HandleKind.TypeReference => IsAvalonia(reader, (TypeReferenceHandle)scope),
                HandleKind.AssemblyReference => reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)scope).Name).StartsWith("Avalonia", StringComparison.Ordinal),
                _ => false,
            };
        }

        public bool GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => IsAvalonia(reader, handle);

        public bool GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        public bool GetGenericInstantiation(bool genericType, ImmutableArray<bool> typeArguments) => genericType || typeArguments.Contains(true);

        public bool GetFunctionPointerType(MethodSignature<bool> signature) => _Signature(signature);

        public bool GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => false;

        public bool GetPrimitiveType(PrimitiveTypeCode typeCode) => false;

        public bool GetGenericMethodParameter(object? genericContext, int index) => false;

        public bool GetGenericTypeParameter(object? genericContext, int index) => false;

        public bool GetSZArrayType(bool elementType) => elementType;

        public bool GetArrayType(bool elementType, ArrayShape shape) => elementType;

        public bool GetByReferenceType(bool elementType) => elementType;

        public bool GetPointerType(bool elementType) => elementType;

        public bool GetPinnedType(bool elementType) => elementType;

        public bool GetModifiedType(bool modifier, bool unmodifiedType, bool isRequired) => unmodifiedType;
    }
}

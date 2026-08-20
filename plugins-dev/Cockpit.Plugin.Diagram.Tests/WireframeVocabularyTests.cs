using System.ComponentModel;
using System.Reflection;
using System.Text.RegularExpressions;
using Cockpit.Core.Wireframe.Model;

namespace Cockpit.Plugin.Diagram.Tests;

// AC-903: the enum is the vocabulary, and the two places that repeat it for a reader — the format doc an operator
// reads and the tool description an agent reads — have to say the same thing or one of them is lying.
public class WireframeVocabularyTests
{
    public static TheoryData<WireframeNodeKind> Kinds =>
        new(Enum.GetValues<WireframeNodeKind>().Where(kind => kind != WireframeNodeKind.Screen));

    [Theory]
    [MemberData(nameof(Kinds))]
    public void EveryKeyword_IsInAddComponentsDescription(WireframeNodeKind kind)
    {
        var description = typeof(WireframeMcpTools)
            .GetMethod(nameof(WireframeMcpTools.AddComponent))!
            .GetCustomAttribute<DescriptionAttribute>()!
            .Description;

        var words = Regex.Split(description.ToLowerInvariant(), "[^a-z]+").ToHashSet(StringComparer.Ordinal);
        Assert.Contains(kind.ToString().ToLowerInvariant(), words);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void EveryKeyword_IsInTheFormatDoc(WireframeNodeKind kind) =>
        Assert.Contains($"`{kind.ToString().ToLowerInvariant()}`", _FormatDoc(), StringComparison.Ordinal);

    // AC-907 val #5: WireframeNodeKind has WireframeVocabularyTests above; WireframeModifierName had nothing
    // comparable, so a new modifier could land half-described without any test noticing.
    public static TheoryData<WireframeModifierName> Modifiers => new(Enum.GetValues<WireframeModifierName>());

    [Theory]
    [MemberData(nameof(Modifiers))]
    public void EveryModifierKeyword_IsInSetComponentModifiersDescription(WireframeModifierName name)
    {
        var method = typeof(WireframeMcpTools).GetMethod(nameof(WireframeMcpTools.SetComponentModifier))!;
        var description = method.GetCustomAttribute<DescriptionAttribute>()!.Description;
        var parameterDescription = method.GetParameters()
            .Single(parameter => parameter.Name == "modifier")
            .GetCustomAttribute<DescriptionAttribute>()!.Description;

        var words = Regex.Split((description + " " + parameterDescription).ToLowerInvariant(), "[^a-z]+").ToHashSet(StringComparer.Ordinal);
        Assert.Contains(name.ToString().ToLowerInvariant(), words);
    }

    [Theory]
    [MemberData(nameof(Modifiers))]
    public void EveryModifierKeyword_IsInTheFormatDocsModifierTable(WireframeModifierName name) =>
        Assert.Contains($"`{name.ToString().ToLowerInvariant()}", _FormatDoc(), StringComparison.Ordinal);

    // The doc is a repository file rather than a build artefact, so it is found by walking up from wherever the
    // test binary landed.
    private static string _FormatDoc()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "docs", "wireframe-format.md")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory.FullName, "docs", "wireframe-format.md"));
    }
}

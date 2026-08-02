using System.ComponentModel;
using System.Reflection;
using Cockpit.Infrastructure.Assistant;
using ModelContextProtocol.Server;

namespace Cockpit.Infrastructure.Tests.Assistant;

/// <summary>
/// The claims the acting tools make about approval, checked against the one sentence that qualifies them. A
/// description promising a click that never comes is a defect the compiler cannot see and no gateway test reaches.
/// </summary>
/// <remarks>
/// The class remarks on <c>AssistantAgentMcpTools</c> say the caveat is "written once because five copies of it are
/// five places for it to stop being true" — and AC-592 then added two tools that promised the row without it. This
/// derives the tool set from the class rather than listing it, so the sixth tool to promise a click is caught on the
/// day it is written.
/// </remarks>
public sealed class AssistantActToolDescriptionTests
{
    [Fact]
    public void EveryActingToolThatPromisesAnApprovalRow_AlsoSaysTheAskingCanBeSwitchedOff()
    {
        var caveat = _TheSharedCaveat();

        var promisingWithoutIt = _EveryDescribedTool()
            .Where(tool => tool.Description.Contains("Allow/Deny", StringComparison.Ordinal))
            .Where(tool => !tool.Description.Contains(caveat, StringComparison.Ordinal))
            .Select(tool => tool.Name)
            .ToArray();

        Assert.Empty(promisingWithoutIt);
    }

    [Fact]
    public void TheCaveatIsCarriedBySomething_SoAnEmptySweepMeansAgreementRatherThanAnEmptySet()
    {
        var caveat = _TheSharedCaveat();

        Assert.NotEmpty(_EveryDescribedTool()
            .Where(tool => tool.Description.Contains(caveat, StringComparison.Ordinal))
            .ToArray());
    }

    private static IReadOnlyList<(string Name, string Description)> _EveryDescribedTool() =>
        [.. typeof(AssistantAgentMcpTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(tool => tool.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .Select(tool => (tool.Name, tool.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty))
            .OrderBy(tool => tool.Name, StringComparer.Ordinal)];

    /// <summary>Read off the class rather than retyped here, so rewording the sentence cannot fail these tests.</summary>
    private static string _TheSharedCaveat()
    {
        var field = typeof(AssistantAgentMcpTools)
            .GetField("AskingCanBeSwitchedOff", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);

        var caveat = field.GetRawConstantValue() as string;
        Assert.False(string.IsNullOrWhiteSpace(caveat));
        return caveat;
    }
}

using FluentAssertions;

namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// <see cref="ScopingVerdict.Parse"/> — reading the delegated judge's answer (AC-151): WORKABLE, REFUSE with a reason,
/// or anything off-script treated as workable so a stray answer never blocks an explicitly started point.
/// </summary>
public class ScopingVerdictTests
{
    [Fact]
    public void Parse_Workable_IsWorkable()
    {
        ScopingVerdict.Parse("WORKABLE").IsWorkable.Should().BeTrue();
    }

    [Fact]
    public void Parse_Refuse_KeepsTheReason()
    {
        var verdict = ScopingVerdict.Parse("REFUSE: no acceptance criteria\nsome extra reasoning");

        verdict.IsWorkable.Should().BeFalse();
        verdict.Reason.Should().Be("no acceptance criteria");
    }

    [Fact]
    public void Parse_RefuseWithoutAReason_FallsBackToADefault()
    {
        var verdict = ScopingVerdict.Parse("REFUSE");

        verdict.IsWorkable.Should().BeFalse();
        verdict.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Parse_OffScriptAnswer_ReadsAsWorkable()
    {
        ScopingVerdict.Parse("I think this is probably fine to do.").IsWorkable.Should().BeTrue();
    }
}

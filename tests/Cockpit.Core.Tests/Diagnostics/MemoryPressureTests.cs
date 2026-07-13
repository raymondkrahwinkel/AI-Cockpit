using Cockpit.Core.Diagnostics;
using FluentAssertions;

namespace Cockpit.Core.Tests.Diagnostics;

/// <summary>
/// When to say that memory is getting tight (#78). The warning exists because macOS charges the memory of everything
/// the cockpit spawns — three Claude sessions is more Node than the whole app — to the cockpit, and kills the cockpit
/// when it fires. Being told beforehand is the difference between closing a session and losing all of them.
/// <para>
/// Every rule here is about being believed. It warns once on the way up, stays quiet while the operator decides, and
/// says nothing at all on numbers that mean nothing — a warning that fires on an idle cockpit is a warning you switch
/// off, and then it is not there on the day it matters.
/// </para>
/// </summary>
public class MemoryPressureTests
{
    private const long Gb = 1024L * 1024 * 1024;

    [Fact]
    public void PastTwoThirdsOfTheMachine_ItWarns()
    {
        var decision = MemoryPressure.Decide(usedBytes: 11 * Gb, totalBytes: 16 * Gb, warned: false);

        decision.Warn.Should().BeTrue();
        decision.Warned.Should().BeTrue("so the next sample does not say it again");
    }

    [Fact]
    public void HavingSaidItOnce_ItDoesNotRepeatWhileYouDecide()
    {
        var decision = MemoryPressure.Decide(usedBytes: 12 * Gb, totalBytes: 16 * Gb, warned: true);

        decision.Warn.Should().BeFalse("a warning every ten seconds is a warning you turn off");
        decision.Warned.Should().BeTrue();
    }

    [Fact]
    public void OnceMemoryHasFallenWellBack_TheNextClimbIsWorthSayingAgain()
    {
        var calm = MemoryPressure.Decide(usedBytes: 8 * Gb, totalBytes: 16 * Gb, warned: true);

        calm.Warn.Should().BeFalse();
        calm.Warned.Should().BeFalse("it is let off the hook, so a real climb later is heard");

        MemoryPressure.Decide(usedBytes: 11 * Gb, totalBytes: 16 * Gb, calm.Warned).Warn.Should().BeTrue();
    }

    [Fact]
    public void JustDippingUnderTheLine_DoesNotResetIt()
    {
        // Otherwise a session that breathes in and out around the threshold warns you twice a minute.
        MemoryPressure.Decide(usedBytes: (long)(10.4 * Gb), totalBytes: 16 * Gb, warned: true)
            .Should().Be(new MemoryPressureDecision(false, true));
    }

    [Fact]
    public void OnASmallMachine_ASmallNumberIsNotAWarning()
    {
        // Two thirds of 4 GB is reached by opening a browser. Below the floor, nothing is said whatever the share.
        MemoryPressure.Decide(usedBytes: (long)(2.8 * Gb), totalBytes: 4 * Gb, warned: false).Warn.Should().BeFalse();
    }

    [Fact]
    public void WhenTheMachinesMemoryIsUnknown_NothingIsWarnedAbout() =>
        // A share of an unknown total is not a fact.
        MemoryPressure.Decide(usedBytes: 12 * Gb, totalBytes: 0, warned: false).Warn.Should().BeFalse();

    [Fact]
    public void AnIdleCockpit_SaysNothing() =>
        MemoryPressure.Decide(usedBytes: 300L * 1024 * 1024, totalBytes: 16 * Gb, warned: false).Warn.Should().BeFalse();

    [Fact]
    public void TheFigureTurnsAmberBeforeAnybodyIsInterrupted() =>
        // A colour is something you can act on quietly. A toast is an interruption, and it is only worth one when the
        // machine is actually close to killing something.
        MemoryPressure.Level(usedBytes: 9 * Gb, totalBytes: 16 * Gb).Should().Be(MemoryPressureLevel.Elevated);

    [Fact]
    public void AtThePointTheWarningFires_TheFigureIsRed() =>
        MemoryPressure.Level(usedBytes: 11 * Gb, totalBytes: 16 * Gb).Should().Be(MemoryPressureLevel.High);

    [Fact]
    public void AnIdleCockpit_ReadsAsCalm() =>
        MemoryPressure.Level(usedBytes: 400L * 1024 * 1024, totalBytes: 16 * Gb).Should().Be(MemoryPressureLevel.Calm);

    [Fact]
    public void WithNoMachineToCompareAgainst_ItReadsAsCalm_RatherThanAsAlarm() =>
        MemoryPressure.Level(usedBytes: 12 * Gb, totalBytes: 0).Should().Be(MemoryPressureLevel.Calm);
}

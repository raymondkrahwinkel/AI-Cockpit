using Cockpit.App.ViewModels;

namespace Cockpit.Core.Tests.Sessions;

/// <summary>
/// What the "pick another moment" dialog starts from and hands back (AC-369). The pickers speak wall-clock time,
/// so everything here is about which zone that wall clock belongs to — the operator's, never the caller's.
/// </summary>
public class ScheduleResumeDialogTests
{
    // A zone that is not UTC and that moves twice a year, because both are what the bug needed to show itself:
    // CI runs on UTC, where a suggestion carrying +00:00 is already local and every one of these tests would
    // pass whatever the code did. Read from the system database rather than hand-built, so the transitions are
    // the real ones and not a rule of my own invention.
    private static readonly TimeZoneInfo Amsterdam = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");

    // Somewhere that is not this machine, whichever machine this is. A Local-kind wall clock only clashes with
    // an offset that disagrees with it, so a fixed zone would prove nothing on the machine it names.
    private static readonly TimeZoneInfo Elsewhere = TimeZoneInfo.CreateCustomTimeZone(
        "ac-369-elsewhere", _OffsetUnlike(TimeZoneInfo.Local), "Elsewhere", "Elsewhere");

    private static TimeSpan _OffsetUnlike(TimeZoneInfo zone)
    {
        var here = zone.GetUtcOffset(DateTimeOffset.UnixEpoch);

        return here < TimeSpan.Zero ? here + TimeSpan.FromHours(1) : here - TimeSpan.FromHours(1);
    }

    private static ScheduleResumeDialogViewModel Open(DateTimeOffset suggested) =>
        new(suggested, "continue", Amsterdam);

    [Fact]
    public void ASuggestionCarryingUtc_PreloadsTheOperatorsOwnWallClock()
    {
        // An allowance reset reaches the dialog through DateTimeOffset.FromUnixTimeSeconds, so it carries
        // offset +00:00 no matter where the operator is. 11:11 UTC is 13:11 to someone in Amsterdam in July,
        // and 13:11 is what the pickers have to say.
        var dialog = Open(new DateTimeOffset(2026, 7, 27, 11, 11, 0, TimeSpan.Zero));

        Assert.Equal(new DateTime(2026, 7, 27), dialog.Day);
        Assert.Equal(new TimeSpan(13, 11, 0), dialog.TimeOfDay);
    }

    [Fact]
    public void ASuggestionThatFallsOnTheNextDayLocally_PreloadsThatDay()
    {
        // Late enough in the evening UTC and the local date is already tomorrow. The calendar picker showing
        // the wrong day is the loudest face of this bug, and it is a separate failure from the wrong time.
        var dialog = Open(new DateTimeOffset(2026, 7, 27, 23, 30, 0, TimeSpan.Zero));

        Assert.Equal(new DateTime(2026, 7, 28), dialog.Day);
        Assert.Equal(new TimeSpan(1, 30, 0), dialog.TimeOfDay);
    }

    [Fact]
    public void AnUntouchedSuggestion_ComesBackAsTheSameMoment()
    {
        // The operator opens the dialog and presses Schedule without changing anything: what gets scheduled has
        // to be the moment that was suggested. This is the whole failure — it used to come back two hours early,
        // which is in the past, which is a resume that never fires.
        var suggested = new DateTimeOffset(2026, 7, 27, 11, 11, 0, TimeSpan.Zero);

        Assert.Equal(suggested, Open(suggested).Moment);
    }

    [Fact]
    public void AnUntouchedSuggestionInTheHourThatRunsTwice_ComesBackAsTheSameMoment()
    {
        // 02:30 on 2026-10-25 happens twice in Amsterdam, an hour apart, and a wall clock cannot say which one
        // is meant. Reading the pickers alone settles on the second pass; the suggestion says it was the first.
        var suggested = new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero);

        Assert.True(Amsterdam.IsAmbiguousTime(new DateTime(2026, 10, 25, 2, 30, 0)));
        Assert.Equal(suggested, Open(suggested).Moment);
    }

    [Fact]
    public void ASuggestionAnHourOut_IsAcceptedAsBeingInTheFuture()
    {
        // The gate the dialog's Schedule button reads. Before the fix this said "no" for any reset less than
        // the operator's own offset away, so the button quietly did nothing at all.
        var dialog = Open(DateTimeOffset.UtcNow.AddHours(1));

        Assert.True(dialog.IsInTheFuture);
    }

    [Fact]
    public void ADayCarryingTheMachinesOwnKind_IsStillReadInThePickersZone()
    {
        // The calendar picker hands back a DateTime that is Local as often as not, and pairing that kind with
        // an offset it disagrees with is something DateTimeOffset refuses outright rather than converting.
        // A different day, not the one already loaded: DateTime equality ignores Kind, so re-assigning the same
        // date in another kind is a no-op the observable property never stores.
        var dialog = new ScheduleResumeDialogViewModel(
            new DateTimeOffset(2026, 7, 27, 11, 11, 0, TimeSpan.Zero), "continue", Elsewhere);

        dialog.Day = DateTime.SpecifyKind(new DateTime(2026, 7, 28), DateTimeKind.Local);

        Assert.Equal(DateTimeKind.Local, dialog.Day.Kind);
        Assert.Equal(new DateTimeOffset(2026, 7, 28, 11, 11, 0, Elsewhere.BaseUtcOffset), dialog.Moment);
    }

    [Fact]
    public void AMomentOnTheDayTheClocksGoBack_TakesTheOffsetOfThatMoment()
    {
        // 2026-10-25 starts on CEST and ends on CET. An afternoon on that day is CET, so reading the offset off
        // midnight — which is still CEST — would schedule an hour early.
        var dialog = Open(new DateTimeOffset(2026, 10, 25, 11, 11, 0, TimeSpan.Zero));

        Assert.NotEqual(
            Amsterdam.GetUtcOffset(new DateTime(2026, 10, 25)),
            Amsterdam.GetUtcOffset(new DateTime(2026, 10, 25, 12, 11, 0)));

        Assert.Equal(new TimeSpan(12, 11, 0), dialog.TimeOfDay);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 11, 11, 0, TimeSpan.Zero), dialog.Moment);
    }

    [Fact]
    public void AMomentOnTheDayTheClocksGoForward_TakesTheOffsetOfThatMoment()
    {
        // The mirror image: 2026-03-29 starts on CET and ends on CEST, so an afternoon is CEST while midnight
        // is not, and midnight's offset would schedule an hour late.
        var dialog = Open(new DateTimeOffset(2026, 3, 29, 11, 11, 0, TimeSpan.Zero));

        Assert.NotEqual(
            Amsterdam.GetUtcOffset(new DateTime(2026, 3, 29)),
            Amsterdam.GetUtcOffset(new DateTime(2026, 3, 29, 13, 11, 0)));

        Assert.Equal(new TimeSpan(13, 11, 0), dialog.TimeOfDay);
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 11, 11, 0, TimeSpan.Zero), dialog.Moment);
    }

    [Fact]
    public void AnHourThatTheClockChangeSkips_StillYieldsAMoment()
    {
        // Nothing stops the operator typing 02:30 on the morning the clocks jump from 02:00 to 03:00 — a wall
        // clock that does not occur. There is no right answer to give, but there are two wrong ones: throwing
        // out of a property the Schedule button reads, and resolving backwards to before the clocks moved —
        // which for a resume waiting on an allowance is the direction that does not fire.
        var dialog = Open(new DateTimeOffset(2026, 3, 29, 6, 0, 0, TimeSpan.Zero));

        dialog.TimeOfDay = new TimeSpan(2, 30, 0);

        Assert.True(Amsterdam.IsInvalidTime(new DateTime(2026, 3, 29, 2, 30, 0)));
        Assert.True(dialog.Moment >= new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void AMomentTheOperatorMovedForward_IsStillReadInTheirOwnZone()
    {
        // The point of the dialog: a week that returns at 11:00 on a Saturday is no use to someone who will not
        // be back until Monday. Moving the day must not quietly move the reading back into the caller's offset.
        var dialog = Open(new DateTimeOffset(2026, 7, 25, 9, 0, 0, TimeSpan.Zero));

        dialog.Day = new DateTime(2026, 7, 27);
        dialog.TimeOfDay = new TimeSpan(8, 30, 0);

        Assert.Equal(new DateTimeOffset(2026, 7, 27, 6, 30, 0, TimeSpan.Zero), dialog.Moment);
    }
}

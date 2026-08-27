using CommunityToolkit.Mvvm.ComponentModel;

namespace Cockpit.App.ViewModels;

// Picking a moment and a prompt for a resume by hand (AC-231) — the route that does not start from a warning,
// for when you know you will not be at the desk.
public sealed partial class ScheduleResumeDialogViewModel : ObservableObject
{
    private readonly TimeZoneInfo _zone;
    private readonly DateTimeOffset _suggested;

    public ScheduleResumeDialogViewModel()
        : this(DateTimeOffset.Now.AddHours(1), "continue")
    {
    }

    // `suggested`: The moment to start from, in whatever offset its caller happens to carry.
    // `prompt`: What the resume would send.
    // `zone`: The zone the pickers speak in; the machine's own unless a test says otherwise.
    public ScheduleResumeDialogViewModel(DateTimeOffset suggested, string prompt, TimeZoneInfo? zone = null)
    {
        _zone = zone ?? TimeZoneInfo.Local;

        // An allowance reset is read off a Unix timestamp and therefore carries offset +00:00 (AC-369); taking
        // .Date/.TimeOfDay straight off it would put UTC's wall clock in the pickers, which Moment then reads back as
        // local — two hours before the reset in summer here, so a resume that never fires.
        _suggested = TimeZoneInfo.ConvertTime(suggested, _zone);

        _day = _suggested.Date;
        _timeOfDay = _suggested.TimeOfDay;
        _prompt = prompt;
    }

    // The day to pick up on — today unless the moment has already gone by.
    [ObservableProperty]
    private DateTime _day;

    // The time of day to pick up at.
    [ObservableProperty]
    private TimeSpan _timeOfDay;

    // What to send. Starts on a plain continue, because that is what picking up where you left off means.
    [ObservableProperty]
    private string _prompt;

    // The chosen moment, as the two pickers together describe it in the operator's own zone.
    public DateTimeOffset Moment
    {
        get
        {
            // A reading in _zone, which is not necessarily this machine's zone, so the kind has to say
            // "unspecified": DateTimeOffset refuses a Local kind paired with a foreign offset rather than
            // converting it.
            var wall = DateTime.SpecifyKind(Day.Date + TimeOfDay, DateTimeKind.Unspecified);

            // The hour that runs twice when the clocks go back names two instants, and GetUtcOffset settles on
            // the later one. Untouched pickers do not have to guess: the caller already said which of the two
            // it meant, and handing back a different instant than the one that was suggested is the whole bug.
            if (wall == _suggested.DateTime && _zone.IsAmbiguousTime(wall))
            {
                return _suggested;
            }

            // Otherwise the offset belongs to the moment being picked, not to midnight of the day it falls on:
            // on that same night 13:11 is CET while 00:00 is still CEST, and carrying midnight's offset over
            // would schedule an hour out.
            return new DateTimeOffset(wall, _zone.GetUtcOffset(wall));
        }
    }

    // Whether the chosen moment is still ahead — scheduling something for the past would never fire.
    public bool IsInTheFuture => Moment > DateTimeOffset.Now;
}

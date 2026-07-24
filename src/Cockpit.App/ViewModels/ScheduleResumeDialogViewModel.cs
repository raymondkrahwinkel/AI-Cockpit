using CommunityToolkit.Mvvm.ComponentModel;

namespace Cockpit.App.ViewModels;

/// <summary>
/// Picking a moment and a prompt for a resume by hand (AC-231) — the route that does not start from a warning,
/// for when you know you will not be at the desk.
/// </summary>
public sealed partial class ScheduleResumeDialogViewModel : ObservableObject
{
    public ScheduleResumeDialogViewModel()
        : this(DateTimeOffset.Now.AddHours(1), "continue")
    {
    }

    public ScheduleResumeDialogViewModel(DateTimeOffset suggested, string prompt)
    {
        _day = suggested.Date;
        _timeOfDay = suggested.TimeOfDay;
        _prompt = prompt;
    }

    /// <summary>The day to pick up on — today unless the moment has already gone by.</summary>
    [ObservableProperty]
    private DateTime _day;

    /// <summary>The time of day to pick up at.</summary>
    [ObservableProperty]
    private TimeSpan _timeOfDay;

    /// <summary>What to send. Starts on a plain continue, because that is what picking up where you left off means.</summary>
    [ObservableProperty]
    private string _prompt;

    /// <summary>The chosen moment in local time, as the two pickers together describe it.</summary>
    public DateTimeOffset Moment => new DateTimeOffset(Day.Date, TimeZoneInfo.Local.GetUtcOffset(Day.Date)) + TimeOfDay;

    /// <summary>Whether the chosen moment is still ahead — scheduling something for the past would never fire.</summary>
    public bool IsInTheFuture => Moment > DateTimeOffset.Now;
}

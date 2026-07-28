namespace Cockpit.Plugin.FanOut;

/// <summary>
/// Turns the operator's one task and an arm's angle into the opening message that arm's session is started
/// with. The angle is named as an angle rather than run on after the task, because a fan-out whose arms read
/// the same brief tends to produce the same answer — the point of the run is that they diverge.
/// </summary>
public static class FanOutBrief
{
    public static string Compose(string task, string angle)
    {
        var brief = task.Trim();
        var slant = angle.Trim();

        return slant.Length == 0 ? brief : $"{brief}{Environment.NewLine}{Environment.NewLine}Take this angle: {slant}";
    }

    /// <summary>
    /// A short name for the whole run, recorded on every session it starts so its cost can be read back as one
    /// run rather than a handful of unrelated sessions. The task's first line, cut where it stops being a label.
    /// </summary>
    public static string Label(string task)
    {
        var firstLine = task.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;

        return firstLine.Length <= MaxLabelLength ? firstLine : $"{firstLine[..MaxLabelLength].TrimEnd()}…";
    }

    private const int MaxLabelLength = 60;
}

using System.Globalization;

namespace Cockpit.Core.Sessions;

// AC-1088: the cap on how much of one tool output the host keeps in memory. Head and tail are kept and the
// middle is dropped, so a row still reads as itself; the full text stays readable from the CLI's own JSONL.
// A cap in characters rather than in rows: 2.000 short rows are cheap, twenty 5 MB `Read`s are not.
public static class ToolOutputBudget
{
    // 64 Ki characters — 128 kB per value, since .NET strings are UTF-16. Twenty 5 MB reads cost ~2,5 MB
    // instead of ~200 MB, and everything a person actually reads on a row fits well inside it.
    public const int MaxChars = 64 * 1024;

    private const int HeadChars = MaxChars / 2;

    public static bool Exceeds(string? value) => value is { Length: > MaxChars };

    // The value as the host keeps it: unchanged under the cap, head + marker + tail over it. `reportedTotal` is
    // for a value clamped more than once — streamed text, re-clamped per delta — where `value` is already
    // clamped and only the caller still knows how much really went through it.
    public static string Clamp(string? value, int? reportedTotal = null)
    {
        if (value is null || value.Length <= MaxChars)
        {
            return value ?? string.Empty;
        }

        var head = _HeadEnd(value, HeadChars);
        var tailStart = _TailStart(value, value.Length - (MaxChars - head));
        var total = reportedTotal is { } stated && stated > value.Length ? stated : value.Length;

        return string.Concat(
            value.AsSpan(0, head),
            Marker(total - head - (value.Length - tailStart), total),
            value.AsSpan(tailStart));
    }

    // What stands in for the dropped middle. Grouped in the operator's own locale, the same as the size the row
    // states next to it — one number shown two ways in one row reads as two different numbers.
    public static string Marker(int omittedChars, int totalChars) => string.Format(
        CultureInfo.CurrentCulture,
        "\n\n[… {0:N0} characters omitted — {1:N0} in the full result …]\n\n",
        omittedChars,
        totalChars);

    // Never end the head on a high surrogate: the other half of that pair is in the dropped middle, and a lone
    // surrogate is not valid text — it survives as a replacement character right up until something serialises it.
    private static int _HeadEnd(string value, int end) =>
        end > 0 && char.IsHighSurrogate(value[end - 1]) ? end - 1 : end;

    // The same cut from the other side: a tail may not open on the low half of a pair whose high half was dropped.
    private static int _TailStart(string value, int start) =>
        start < value.Length && char.IsLowSurrogate(value[start]) ? start + 1 : start;
}

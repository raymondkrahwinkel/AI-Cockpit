using System.Text.RegularExpressions;

namespace Cockpit.Core.Sessions;

// The shape a ticket id takes everywhere this codebase already writes one by hand ("AC-13", "AC-544"): two or more
// uppercase letters, a dash, digits. One pattern for every reader (AC-544, AC-1717) rather than one per caller (AC-251).
public static class TicketId
{
    public static Regex Pattern { get; } = new(@"^[A-Z]{2,}-\d+\b", RegexOptions.Compiled);

    // The ticket `text` starts with, else null.
    public static string? At(string? text) =>
        !string.IsNullOrWhiteSpace(text) && Pattern.Match(text.TrimStart()) is { Success: true } match ? match.Value : null;
}

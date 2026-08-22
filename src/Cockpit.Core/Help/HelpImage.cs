namespace Cockpit.Core.Help;

// The result of resolving one `![](...)` reference. Bytes only when they came out of the assembly the page
// itself was shipped in — there is no code path here that opens a socket or touches the filesystem, which is
// what makes "works offline" and "no silent network traffic" the same guarantee.
public sealed record HelpImage(HelpImageOutcome Outcome, byte[]? Bytes)
{
    public static HelpImage Blocked { get; } = new(HelpImageOutcome.BlockedExternal, null);

    public static HelpImage Missing { get; } = new(HelpImageOutcome.Missing, null);
}

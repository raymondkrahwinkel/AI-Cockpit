namespace Cockpit.App;

/// <summary>
/// The colours an operator can mark a screenshot in (AC-375) — ink rather than chrome.
/// </summary>
/// <remarks>
/// Deliberately not theme tokens, and its own file so the exemption reaches nothing else. These are not the
/// cockpit's colours: they go onto somebody else's screen, in a picture that leaves the machine. Pointing them at
/// <c>Theme.axaml</c> would make an operator's red turn into whatever the cockpit's next repaint decided red
/// means, on screenshots already sent. The same argument the stand-in desktop is exempted under.
/// <para>
/// The blue is missing on purpose. It is the theme's accent, read at runtime and handed to the surface, so that
/// the default a mark is drawn in stays the one colour this app does own — and stays in one place, which is the
/// mistake AC-334 spent a ticket undoing.
/// </para>
/// <para>
/// Five is a judgement, not a limit: enough that two marks on one capture can differ, few enough to stay a row of
/// swatches rather than a colour picker. Light and dark are both here, which costs nothing — every mark that
/// needs contrast against what it lies on works that out from its own colour, so a white arrow rings itself in
/// black without being told.
/// </para>
/// </remarks>
internal static class MarkInk
{
    /// <summary>What most people reach for first on a screenshot.</summary>
    public const uint Red = 0xFFE5484D;

    /// <summary>The marker-pen colour — reads as emphasis rather than as an error.</summary>
    public const uint Yellow = 0xFFF5C542;

    public const uint Green = 0xFF30A46C;

    /// <summary>For a capture that is mostly dark, where every ink above still competes with the picture.</summary>
    public const uint White = 0xFFFFFFFF;
}

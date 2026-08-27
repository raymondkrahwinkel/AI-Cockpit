namespace Cockpit.App;

// Fixed screenshot-ink colours must not change with cockpit themes after an image is sent (AC-356/AC-375). Blue comes
// from the runtime accent to keep that owned colour in one place (AC-334); five swatches provide useful contrast
// without becoming a colour picker.
internal static class MarkInk
{
    // What most people reach for first on a screenshot.
    public const uint Red = 0xFFE5484D;

    // The marker-pen colour — reads as emphasis rather than as an error.
    public const uint Yellow = 0xFFF5C542;

    public const uint Green = 0xFF30A46C;

    // For a capture that is mostly dark, where every ink above still competes with the picture.
    public const uint White = 0xFFFFFFFF;
}

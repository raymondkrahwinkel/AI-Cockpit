using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Cockpit.Plugin.Diagram.Collab;

// The handful of visual/IO building blocks every collab surface (ActivityStrip, DiagramWorkspaceBody,
// WhiteboardWorkspaceBody) built its own copy of before AC-870: a themed-resource lookup, a capability chip, and
// the "read a file back to compare against what was last saved" helper the save bar on each surface used.
internal static class SurfaceChrome
{
    public static IBrush? Brush(string resourceKey) => Application.Current?.FindResource(resourceKey) as IBrush;

    // AC-974: MaxWidth+TextTrimming bounds the chip's own desired size so a long capability name ellipsizes
    // instead of forcing the coupling bar's info group wider than the space its action buttons need.
    public static TextBlock Chip() => new()
    {
        Margin = new Thickness(6, 0, 0, 0),
        Padding = new Thickness(6, 1),
        FontSize = 10,
        MaxWidth = 160,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    public static void SetChip(TextBlock chip, string name, bool granted)
    {
        chip.Text = granted ? $"{name} allowed" : $"{name} not granted";
        chip.Foreground = granted ? Brush("CockpitAccentBrush") : Brush("CockpitTextSecondaryBrush");
        ToolTip.SetTip(chip, chip.Text);
    }

    // Null (unreadable, or no file yet) means the next save skips the changed-underneath check rather than
    // refusing on a baseline it never had.
    public static string? ReadFile(string? filePath)
    {
        try
        {
            return filePath is not null && File.Exists(filePath) ? File.ReadAllText(filePath) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}

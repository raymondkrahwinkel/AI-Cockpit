using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions.Theming;

namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// The shared per-field status line for a provider's <see cref="IPluginProviderConfigView"/> — the small green/amber
/// "Found: …" / "Not found …" feedback under a config field. It lives in the SDK (unlike the copy-per-plugin
/// <c>SettingsHelpRow</c>) because every provider config view needs exactly the same widget, and the host-shared
/// abstractions assembly is the one place plugins may share code: centralising it here keeps the affordance
/// identical across providers instead of each plugin hard-coding its own brushes. No leading glyph: this assembly
/// deliberately carries no Material.Icons.Avalonia reference (see the csproj), so the colour alone — not a
/// checkmark/cross character — is what tells found from not-found.
/// </summary>
public static class ProviderConfigStatus
{
    /// <summary>Creates an empty status line to place under a config field; fill it with <see cref="Set"/>.</summary>
    public static TextBlock CreateLine() =>
        new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };

    /// <summary>Sets the status text and colour: green when <paramref name="isOk"/>, otherwise amber.</summary>
    public static void Set(TextBlock line, string message, bool isOk)
    {
        line.Text = message;
        // Muted green / amber rather than pure success/error red: a field being "not found" is a warning the operator
        // may knowingly accept (a profile can pin a command for a machine that has it installed elsewhere), not an
        // error. Reuses the same status tokens the session dot paints with, resolved live so a theme swap follows.
        line.Foreground = isOk
            ? ThemeBrush.Resolve("CockpitStatusDoneBrush", "#5AA576")
            : ThemeBrush.Resolve("CockpitStatusWaitingBrush", "#E0A33E");
    }
}

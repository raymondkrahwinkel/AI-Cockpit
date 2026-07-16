using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// The shared per-field status line for a provider's <see cref="IPluginProviderConfigView"/> — the small green/amber
/// "✓ Found: …" / "✗ Not found …" feedback under a config field. It lives in the SDK (unlike the copy-per-plugin
/// <c>SettingsHelpRow</c>) because every provider config view needs exactly the same widget, and the host-shared
/// abstractions assembly is the one place plugins may share code: centralising it here keeps the affordance
/// identical across providers instead of each plugin hard-coding its own brushes and prefixes.
/// </summary>
public static class ProviderConfigStatus
{
    // Muted green / amber rather than pure success/error red: a field being "not found" is a warning the operator
    // may knowingly accept (a profile can pin a command for a machine that has it installed elsewhere), not an error.
    private static readonly IBrush _OkBrush = new SolidColorBrush(Color.Parse("#5AA576"));
    private static readonly IBrush _WarnBrush = new SolidColorBrush(Color.Parse("#E0A33E"));

    /// <summary>Creates an empty status line to place under a config field; fill it with <see cref="Set"/>.</summary>
    public static TextBlock CreateLine() =>
        new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };

    /// <summary>Sets the status text and colour: a leading ✓ in green when <paramref name="isOk"/>, otherwise ✗ in amber.</summary>
    public static void Set(TextBlock line, string message, bool isOk)
    {
        line.Text = (isOk ? "✓ " : "✗ ") + message;
        line.Foreground = isOk ? _OkBrush : _WarnBrush;
    }
}

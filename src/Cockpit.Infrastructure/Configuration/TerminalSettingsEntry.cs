using Cockpit.Core.Terminal;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of `TerminalSettings` in the `terminal` section of `cockpit.json`.
internal sealed class TerminalSettingsEntry
{
    public string FontFamily { get; set; } = "Cascadia Mono, Consolas, monospace";

    public int FontSize { get; set; } = 13;

    public string Shell { get; set; } = string.Empty;

    public static TerminalSettingsEntry FromDomain(TerminalSettings settings) => new()
    {
        FontFamily = settings.FontFamily,
        FontSize = settings.FontSize,
        Shell = settings.Shell,
    };

    public TerminalSettings ToDomain() => new()
    {
        FontFamily = FontFamily,
        FontSize = FontSize,
        Shell = Shell,
    };
}

using Cockpit.Core.Terminal;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>On-disk shape of <see cref="TerminalSettings"/> in the <c>terminal</c> section of <c>cockpit.json</c>.</summary>
internal sealed class TerminalSettingsEntry
{
    public string FontFamily { get; set; } = "Cascadia Mono, Consolas, monospace";

    public int FontSize { get; set; } = 13;

    public static TerminalSettingsEntry FromDomain(TerminalSettings settings) => new()
    {
        FontFamily = settings.FontFamily,
        FontSize = settings.FontSize,
    };

    public TerminalSettings ToDomain() => new()
    {
        FontFamily = FontFamily,
        FontSize = FontSize,
    };
}

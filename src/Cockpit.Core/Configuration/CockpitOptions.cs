namespace Cockpit.Core.Configuration;

public sealed class CockpitOptions
{
    public const string SectionName = "Cockpit";

    public AudioOptions Audio { get; set; } = new();

    public ClaudeCliOptions Claude { get; set; } = new();
}

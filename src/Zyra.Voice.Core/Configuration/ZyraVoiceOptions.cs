namespace Zyra.Voice.Core.Configuration;

public sealed class ZyraVoiceOptions
{
    public const string SectionName = "ZyraVoice";

    public AudioOptions Audio { get; set; } = new();

    public ClaudeCliOptions Claude { get; set; } = new();
}

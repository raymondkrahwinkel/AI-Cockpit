using Cockpit.Core.Abstractions.Updates;
using Cockpit.Core.Updates;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>On-disk shape of <see cref="UpdateSettings"/> in the <c>updates</c> section of <c>cockpit.json</c> (#71).</summary>
internal sealed class UpdateSettingsEntry
{
    public bool CheckOnStartup { get; set; } = true;

    /// <summary>"Stable" or "Nightly". Anything else reads as Stable — an unreadable channel must not quietly opt someone into last night's build of main.</summary>
    public string Channel { get; set; } = nameof(UpdateChannel.Stable);

    public static UpdateSettingsEntry FromDomain(UpdateSettings settings) => new()
    {
        CheckOnStartup = settings.CheckOnStartup,
        Channel = settings.Channel.ToString(),
    };

    public UpdateSettings ToDomain() => new(
        CheckOnStartup,
        Enum.TryParse<UpdateChannel>(Channel, ignoreCase: true, out var channel) ? channel : UpdateChannel.Stable);
}

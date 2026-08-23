using Cockpit.Core.Abstractions.Updates;
using Cockpit.Core.Updates;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of `UpdateSettings` in the `updates` section of `cockpit.json` (#71).
internal sealed class UpdateSettingsEntry
{
    public bool CheckOnStartup { get; set; } = true;

    // AC-387: "Stable"/"Nightly", the channel the operator picked; absent (or unparseable) means nobody
    // chose and the build's own stream decides. Replaces an old `Channel` key every start wrote back
    // regardless, so a pre-change config reads as "never chosen" and the old key drops on next save.
    public string? ChosenChannel { get; set; }

    public static UpdateSettingsEntry FromDomain(UpdateSettings settings) => new()
    {
        CheckOnStartup = settings.CheckOnStartup,
        ChosenChannel = settings.Channel?.ToString(),
    };

    public UpdateSettings ToDomain() => new(
        CheckOnStartup,
        Enum.TryParse<UpdateChannel>(ChosenChannel, ignoreCase: true, out var channel) ? channel : null);
}

using Cockpit.Core.Abstractions.Updates;
using Cockpit.Core.Updates;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>On-disk shape of <see cref="UpdateSettings"/> in the <c>updates</c> section of <c>cockpit.json</c> (#71).</summary>
internal sealed class UpdateSettingsEntry
{
    public bool CheckOnStartup { get; set; } = true;

    /// <summary>
    /// "Stable" or "Nightly" — the channel the operator picked in the Updates tab. Absent means nobody picked one and
    /// the build's own stream decides (AC-387). Anything unreadable is treated the same way: a channel we cannot
    /// parse is not a choice somebody made.
    /// <para>
    /// This replaced a <c>Channel</c> key that every start wrote back whether or not the operator had touched it —
    /// which made the stored value evidence of nothing. Reading that key here would have kept the drift it caused, so
    /// a configuration written before this change is read as "never chosen" and the old key is dropped on the next
    /// save. The cost is a deliberate nightly choice on a stable build having to be made once more; the choice is
    /// permanent after that.
    /// </para>
    /// </summary>
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

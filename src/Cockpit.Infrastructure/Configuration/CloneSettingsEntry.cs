using Cockpit.Core.Clones;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>On-disk shape of <see cref="CloneSettings"/> in the <c>cloneSettings</c> section of <c>cockpit.json</c> (AC-90). Separate from the <c>clones</c> registry section, which lists the clones themselves.</summary>
internal sealed class CloneSettingsEntry
{
    public string? Root { get; set; }

    public static CloneSettingsEntry FromDomain(CloneSettings settings) => new() { Root = settings.Root };

    public CloneSettings ToDomain() => new() { Root = Root };
}

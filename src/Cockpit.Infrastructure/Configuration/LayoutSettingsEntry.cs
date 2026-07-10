using Cockpit.Core.Layout;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>On-disk shape of <see cref="LayoutSettings"/> in the <c>layout</c> section of <c>cockpit.json</c>.</summary>
internal sealed class LayoutSettingsEntry
{
    public bool SingleSessionLayout { get; set; }

    public bool StackSessionsVertically { get; set; }

    public bool MinimizeToTrayOnClose { get; set; }

    public static LayoutSettingsEntry FromDomain(LayoutSettings settings) => new()
    {
        SingleSessionLayout = settings.SingleSessionLayout,
        StackSessionsVertically = settings.StackSessionsVertically,
        MinimizeToTrayOnClose = settings.MinimizeToTrayOnClose,
    };

    public LayoutSettings ToDomain() => new()
    {
        SingleSessionLayout = SingleSessionLayout,
        StackSessionsVertically = StackSessionsVertically,
        MinimizeToTrayOnClose = MinimizeToTrayOnClose,
    };
}

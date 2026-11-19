using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Rendering;
using Cockpit.Infrastructure.Voice;

namespace Cockpit.Infrastructure.Rendering;

internal sealed class DxgiGpuDeviceProvider : IGpuDeviceProvider, ISingletonService
{
    public IReadOnlyList<string> GetDeviceNames() => GpuHardwareProbe.WindowsAdapters()
        .Select(adapter => adapter.Name)
        .Where(name => name.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

namespace Cockpit.Core.Abstractions.Rendering;

public interface IGpuDeviceProvider
{
    IReadOnlyList<string> GetDeviceNames();
}

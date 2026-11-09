namespace Cockpit.Infrastructure.Audio;

internal sealed record AudioDuckingSession(
    string DeviceId,
    string Id,
    string RestoreId,
    int ProcessId,
    bool IsPlaying,
    double[] Volumes);

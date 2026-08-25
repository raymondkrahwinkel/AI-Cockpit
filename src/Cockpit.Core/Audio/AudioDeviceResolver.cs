namespace Cockpit.Core.Audio;

// Pure matching of a persisted device name against the devices currently present: returns the index of
// the match, or -1 to mean "use the system default". Kept separate from the SoundFlow layer so the
// fallback rules are unit-testable without a real audio backend.
public static class AudioDeviceResolver
{
    public static int FindIndex(string? preferredName, IReadOnlyList<string> availableNames)
    {
        if (string.IsNullOrWhiteSpace(preferredName))
        {
            return -1;
        }

        for (var i = 0; i < availableNames.Count; i++)
        {
            if (string.Equals(availableNames[i], preferredName, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}

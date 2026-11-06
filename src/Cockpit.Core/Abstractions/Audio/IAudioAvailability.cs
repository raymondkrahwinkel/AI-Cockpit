namespace Cockpit.Core.Abstractions.Audio;

// Audio is an optional desktop capability: native backends and their OS dependencies may be absent even when the
// rest of Cockpit is healthy. Keeping the result in the common abstraction lets every surface explain the same fact.
public interface IAudioAvailability
{
    bool IsAvailable { get; }

    string? UnavailableReason { get; }
}

public sealed record AudioAvailability(bool IsAvailable, string? UnavailableReason) : IAudioAvailability;

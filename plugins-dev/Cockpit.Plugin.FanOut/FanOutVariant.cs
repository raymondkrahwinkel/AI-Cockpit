namespace Cockpit.Plugin.FanOut;

// One arm of a fan-out: which identity runs the task, and the angle it is asked to take. Both smaller than
// they look — the mechanism is the same whichever of the two varies, which is why one type covers both ways
// of fanning out. Leave `Angle` empty and vary `ProfileId` to put several providers
// on the same brief; keep one profile and vary the angle to get several takes from the same model.
//
// `ProfileId`: The profile's label as the cockpit knows it; empty runs the cockpit's default profile.
// `Angle`: The slant this arm is asked to take, appended to the task. Empty gives it the task unchanged.
public sealed record FanOutVariant(string ProfileId, string Angle)
{
    // What this arm is called on its tile: its angle where it has one, otherwise the profile it runs.
    public string Describe() =>
        Angle.Trim() is { Length: > 0 } angle
            ? angle
            : ProfileId.Trim() is { Length: > 0 } profile ? profile : "Default profile";
}

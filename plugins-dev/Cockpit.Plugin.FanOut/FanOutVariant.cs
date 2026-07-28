namespace Cockpit.Plugin.FanOut;

/// <summary>
/// One arm of a fan-out: which identity runs the task, and the angle it is asked to take. Both smaller than
/// they look — the mechanism is the same whichever of the two varies, which is why one type covers both ways
/// of fanning out. Leave <see cref="Angle"/> empty and vary <see cref="ProfileId"/> to put several providers
/// on the same brief; keep one profile and vary the angle to get several takes from the same model.
/// </summary>
/// <param name="ProfileId">The profile's label as the cockpit knows it; empty runs the cockpit's default profile.</param>
/// <param name="Angle">The slant this arm is asked to take, appended to the task. Empty gives it the task unchanged.</param>
public sealed record FanOutVariant(string ProfileId, string Angle)
{
    /// <summary>What this arm is called on its tile: its angle where it has one, otherwise the profile it runs.</summary>
    public string Describe() =>
        Angle.Trim() is { Length: > 0 } angle
            ? angle
            : ProfileId.Trim() is { Length: > 0 } profile ? profile : "Default profile";
}

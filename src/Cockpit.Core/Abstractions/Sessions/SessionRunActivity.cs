namespace Cockpit.Core.Abstractions.Sessions;

public sealed record SessionRunActivity(
    string RunId,
    string Label,
    string Kind,
    SessionRunState State,
    DateTimeOffset Since,
    string? WaitReason);

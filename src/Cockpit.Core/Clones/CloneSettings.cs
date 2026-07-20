namespace Cockpit.Core.Clones;

/// <summary>
/// Where the cockpit clones repositories from a URL (AC-90). <see cref="Root"/> null or blank keeps the default — a
/// <c>clones/</c> folder under the app state root — while an operator can override it, for example to a faster disk or
/// one with more room; new clones then go there under the same <c>host/org/repo</c> slug. Existing clones keep the
/// absolute path they were made at, so changing this never strands them (same discipline as the worktree root, AC-85).
/// </summary>
public sealed record CloneSettings
{
    public string? Root { get; init; }
}

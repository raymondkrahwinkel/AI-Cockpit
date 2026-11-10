namespace Cockpit.Core.Sessions;

public sealed record PathBoundaryResult(bool IsInside, string? Reason = null);

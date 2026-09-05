namespace Cockpit.Core.Depot;

// The operator's mirrors-root-location override (AC-278), the same shape as `CloneSettings`.
public sealed record DepotMirrorSettings
{
    public string? Root { get; init; }
}

namespace Cockpit.Plugins.Abstractions.Channels;

/// <summary>An actual pending operator decision, supplied by the owner of its callback.</summary>
public sealed record OperatorAction(string Id, string Revision, string Kind, string Target, string Summary, IReadOnlyList<string> Decisions)
{
    public string? Title { get; init; }
    public string? Risk { get; init; }
    public string? SourcePlugin { get; init; }
}

/// <summary>A pending decision and its one-use capability, scoped to one authenticated channel.</summary>
public sealed record OperatorPendingAction(string Id, string Revision, string Kind, string Target, string Summary, IReadOnlyList<string> Decisions, string Capability)
{
    public string? Title { get; init; }
    public string? Risk { get; init; }
    public string? SourcePlugin { get; init; }
}

/// <summary>The host confirmed the decision, or refused an obsolete or unauthorised response.</summary>
public sealed record OperatorActionResult(bool Ok, string? Error);

/// <summary>A transport adapter's revocable authority for one authenticated device; never exposed as an agent tool.</summary>
public interface IOperatorActionChannel : IDisposable
{
    IReadOnlyList<OperatorPendingAction> Snapshot { get; }
    event EventHandler? Changed;
    Task<OperatorActionResult> RespondAsync(string id, string revision, string capability, string decision);
}

/// <summary>Generic host seam between pending decision owners and authenticated operator transports.</summary>
public interface IOperatorActionService
{
    IDisposable Register(OperatorAction action, Func<string, Task<bool>> respond, Func<bool> isCurrent);
}

/// <summary>Host-authorised transport adapter for authenticated operator streams; never available to action publishers.</summary>
public interface IOperatorActionTransport
{
    IOperatorActionChannel OpenChannel();
}

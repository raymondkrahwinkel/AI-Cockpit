using Cockpit.Core.Abstractions.Sessions;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// <see cref="IPermissionServerState"/> test double with settable coordinates, so tests can drive
/// the "server ready" and "server not yet ready" argument-construction branches.
/// </summary>
internal sealed class StubPermissionServerState : IPermissionServerState
{
    public string? McpConfigPath { get; init; }

    public string? PermissionPromptToolName { get; init; }

    public string? PermissionMcpUrl { get; init; }
}

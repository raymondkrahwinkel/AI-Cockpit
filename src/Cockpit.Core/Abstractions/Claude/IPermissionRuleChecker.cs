namespace Cockpit.Core.Abstractions.Claude;

/// <summary>
/// Answers whether a proposed tool call is already covered by an always-allow rule. A session
/// registers its owning profile's checker with the <see cref="IPermissionCoordinator"/> per
/// <c>tool_use_id</c>, so the coordinator can short-circuit a prompt the operator already opted out
/// of — without the app-wide coordinator needing to know which profile owns which session.
/// </summary>
public interface IPermissionRuleChecker
{
    /// <summary>
    /// True when an always-allow rule already covers a call to <paramref name="toolName"/> with
    /// <paramref name="proposedInputJson"/>, meaning the coordinator should allow it without raising a prompt.
    /// </summary>
    bool IsAlwaysAllowed(string toolName, string proposedInputJson);
}

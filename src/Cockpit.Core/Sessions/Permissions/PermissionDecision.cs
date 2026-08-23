namespace Cockpit.Core.Sessions.Permissions;

// The outcome of a permission prompt: allow (optionally with a rewritten input) or deny with a reason.
// Serialized back to the CLI's `--permission-prompt-tool` contract (see `PermissionPromptResponse`).
public sealed record PermissionDecision
{
    private PermissionDecision(bool isAllowed, string? updatedInputJson, string? denyMessage)
    {
        IsAllowed = isAllowed;
        UpdatedInputJson = updatedInputJson;
        DenyMessage = denyMessage;
    }

    // True to let the tool run, false to block it.
    public bool IsAllowed { get; }

    // When allowing, the tool input to run with as a raw JSON object. Null echoes the
    // originally proposed input unchanged.
    public string? UpdatedInputJson { get; }

    // When denying, the reason surfaced to Claude as the tool-result error.
    public string? DenyMessage { get; }

    // Allow the call, running the tool with `updatedInputJson` (or the original input when null).
    public static PermissionDecision Allow(string? updatedInputJson = null) => new(true, updatedInputJson, null);

    // Deny the call; `message` becomes the tool-result error Claude sees.
    public static PermissionDecision Deny(string message) => new(false, null, message);
}

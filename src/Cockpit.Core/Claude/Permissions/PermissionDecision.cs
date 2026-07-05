namespace Cockpit.Core.Claude.Permissions;

/// <summary>
/// The outcome of a permission prompt: allow the tool call to proceed (optionally with a
/// rewritten input) or deny it with a reason. Serialized back to the CLI's
/// <c>--permission-prompt-tool</c> as the <c>behavior</c>/<c>updatedInput</c>/<c>message</c>
/// contract (see <see cref="PermissionPromptResponse"/>).
/// </summary>
public sealed record PermissionDecision
{
    private PermissionDecision(bool isAllowed, string? updatedInputJson, string? denyMessage)
    {
        IsAllowed = isAllowed;
        UpdatedInputJson = updatedInputJson;
        DenyMessage = denyMessage;
    }

    /// <summary>True to let the tool run, false to block it.</summary>
    public bool IsAllowed { get; }

    /// <summary>
    /// When allowing, the tool input to run with as a raw JSON object. Null echoes the
    /// originally proposed input unchanged.
    /// </summary>
    public string? UpdatedInputJson { get; }

    /// <summary>When denying, the reason surfaced to Claude as the tool-result error.</summary>
    public string? DenyMessage { get; }

    /// <summary>Allow the call, running the tool with <paramref name="updatedInputJson"/> (or the original input when null).</summary>
    public static PermissionDecision Allow(string? updatedInputJson = null) => new(true, updatedInputJson, null);

    /// <summary>Deny the call; <paramref name="message"/> becomes the tool-result error Claude sees.</summary>
    public static PermissionDecision Deny(string message) => new(false, null, message);
}

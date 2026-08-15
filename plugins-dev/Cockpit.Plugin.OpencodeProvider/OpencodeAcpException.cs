namespace Cockpit.Plugin.OpencodeProvider;

// Raised when `opencode acp` answers a request with a JSON-RPC `error` object, or the stdio stream ends
// with a request still outstanding — carries the agent's raw error text so the driver can surface it as a
// session error rather than hanging on a reply that will never come.
internal sealed class OpencodeAcpException : Exception
{
    public OpencodeAcpException(string message) : base(message)
    {
    }

    // Carries the JSON-RPC error code as data, separate from the already-sanitised message text (built from
    // only error.code + error.message, never error.GetRawText() — see OpencodeAcpConnection) — so a caller
    // could match on the numeric code instead of parsing it back out of the message string. Unlike
    // KimiAcpSessionDriver, this driver does not match on a specific code for an auth failure: that JSON-RPC
    // code was never exercised live (opencode's built-in free models need no auth at all — see
    // OpencodeAcpSessionDriver's remarks), so guessing one here would be exactly the kind of unmeasured
    // assumption AC-783 asks not to make. The code is still carried, for a future measurement to use.
    public OpencodeAcpException(string message, int code) : base(message) => Code = code;

    // The JSON-RPC error code, when this exception was raised from an agent-returned error object; `null` for
    // the stream-ended-before-reply case, which carries no code of its own.
    public int? Code { get; }
}

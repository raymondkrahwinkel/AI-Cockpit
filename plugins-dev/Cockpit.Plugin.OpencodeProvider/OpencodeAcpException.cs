namespace Cockpit.Plugin.OpencodeProvider;

// Raised when `opencode acp` answers a request with a JSON-RPC `error` object, or the stdio stream ends
// with a request still outstanding — carries the agent's raw error text so the driver can surface it as a
// session error rather than hanging on a reply that will never come.
internal sealed class OpencodeAcpException : Exception
{
    public OpencodeAcpException(string message) : base(message)
    {
    }

    // AC-783: carries the JSON-RPC error code separately from the sanitised message. Unlike Kimi, this driver
    // never matches on a specific code for an auth failure — that path was never exercised live.
    public OpencodeAcpException(string message, int code) : base(message) => Code = code;

    // The JSON-RPC error code, when this exception was raised from an agent-returned error object; `null` for
    // the stream-ended-before-reply case, which carries no code of its own.
    public int? Code { get; }
}

namespace Cockpit.Plugin.KimiProvider;

// Raised when `kimi acp` answers a request with a JSON-RPC `error` object, or the stdio stream
// ends with a request still outstanding — carries the agent's raw error text so the driver can surface it
// as a session error rather than hanging on a reply that will never come.
internal sealed class KimiAcpException : Exception
{
    public KimiAcpException(string message) : base(message)
    {
    }

    // P1-10b: carries the JSON-RPC error code as data, separate from the already-sanitised message text (P1-8:
    // built from only error.code + error.message, never error.GetRawText()) — so a caller such as StartAsync's
    // authRequired handling can match on the numeric code instead of parsing it back out of the message string.
    public KimiAcpException(string message, int code) : base(message) => Code = code;

    // The JSON-RPC error code, when this exception was raised from an agent-returned error object; `null` for the stream-ended-before-reply case, which carries no code of its own.
    public int? Code { get; }
}

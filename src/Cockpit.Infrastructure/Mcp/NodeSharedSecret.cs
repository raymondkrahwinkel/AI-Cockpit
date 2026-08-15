using Cockpit.Core.Abstractions;

namespace Cockpit.Infrastructure.Mcp;

// The credential the node listener accepts right now (AC-790's secret, made live by AC-792).
//
// AC-790 could hand `McpAuthMiddleware` a plain string read once at mount time, because the only way that value
// ever changed was by editing `cockpit.json` and restarting. Pairing changes it while the process runs — confirming
// mints a new one, unpairing removes it — and a middleware holding the startup copy would mean two silent lies:
// a freshly paired controller turned away until the node restarts, and an unpaired one still let in until then.
// The second is the serious one. Revocation that takes effect at the next launch is not revocation.
//
// A holder rather than re-reading the store per request: the store reads and decrypts the whole of `cockpit.json`,
// which is not work to do on the path of every MCP call. Whoever changes the secret changes it here too, in the
// same act — which is why both writers are one method apart in `NodePairingBroker`.
internal sealed class NodeSharedSecret : ISingletonService
{
    private volatile string? _value;

    // Null means "no credential" — an unpaired node with the master switch on listens and refuses everything,
    // which is the correct posture rather than an error state.
    public string? Value => _value;

    public void Set(string? value) => _value = string.IsNullOrEmpty(value) ? null : value;
}

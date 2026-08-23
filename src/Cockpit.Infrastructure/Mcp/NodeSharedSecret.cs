using Cockpit.Core.Abstractions;

namespace Cockpit.Infrastructure.Mcp;

// AC-790's secret, made live by AC-792: the credential the node listener accepts right now. A plain string read
// once at mount time would mean revocation only at the next launch — pairing changes this live (mint on
// confirm, remove on unpair). A holder, not a re-read per request, since decrypting cockpit.json is too costly.
internal sealed class NodeSharedSecret : ISingletonService
{
    private volatile string? _value;

    // Null means "no credential" — an unpaired node with the master switch on listens and refuses everything,
    // which is the correct posture rather than an error state.
    public string? Value => _value;

    public void Set(string? value) => _value = string.IsNullOrEmpty(value) ? null : value;
}

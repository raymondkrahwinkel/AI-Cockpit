using System.Security.Cryptography;
using Cockpit.Core.Abstractions;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Mcp;

// This machine's stable id in a discovery announce (AC-793) — not a credential, just enough that a finder's
// "nodes found" list does not grow a new row every time the same node answers a second query. Same shape as
// `NodeSelfSignedCertificate` (lazy load-or-create under a lock, one file, regenerate if unreadable) minus
// everything that shape needed only because a certificate is a credential: no validity window, no PKCS#12, no
// refusal semantics for a stale value — sixteen random bytes are either there or they are not.
internal sealed class NodeDiscoveryId : ISingletonService
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private string? _value;

    public NodeDiscoveryId()
        : this(CockpitConfigPath.NodeDiscoveryId)
    {
    }

    // Test seam: point the id at an arbitrary file.
    internal NodeDiscoveryId(string path)
    {
        _path = path;
    }

    public string Value
    {
        get
        {
            lock (_gate)
            {
                return _value ??= _LoadOrCreate();
            }
        }
    }

    private string _LoadOrCreate()
    {
        if (_TryLoad() is { } existing)
        {
            return existing;
        }

        var minted = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

        try
        {
            CockpitConfigPath.WriteAllTextPrivate(_path, minted);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A node that cannot write its id still answers discovery this run — just with an id that will not
            // survive a restart, which only costs a finder one duplicate-looking row, not a broken feature.
        }

        return minted;
    }

    private string? _TryLoad()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            var existing = File.ReadAllText(_path).Trim();
            return existing.Length == 0 ? null : existing;
        }
        catch (IOException)
        {
            return null;
        }
    }
}

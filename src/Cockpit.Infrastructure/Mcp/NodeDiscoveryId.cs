using System.Security.Cryptography;
using Cockpit.Core.Abstractions;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Mcp;

// AC-793: this machine's stable discovery-announce id — not a credential, just enough that a finder's list
// doesn't grow a new row per query. Same lazy load-or-create shape as NodeSelfSignedCertificate, minus
// everything that shape needs only because a certificate is a credential.
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

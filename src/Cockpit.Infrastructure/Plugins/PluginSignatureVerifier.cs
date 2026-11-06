using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;

namespace Cockpit.Infrastructure.Plugins;

internal sealed class PluginSignatureVerifier : IPluginSignatureVerifier, ISingletonService
{
    public PluginSignatureCheck Verify(byte[] statement, string armored)
    {
        var result = SshSig.Verify(statement, armored);
        return new PluginSignatureCheck(result.Ok, result.Reason, result.Fingerprint);
    }

    public bool IsValidIdentity(string id, string version) =>
        id is not null && version is not null && SshSig.IsValidId(id) && SshSig.IsValidVersion(version);

    public byte[] Statement(string organization, string id, string version, string platform, string sha256, PluginStoreSource? source) =>
        SshSig.Statement(organization, id, version, platform, sha256, source?.Repository ?? "", source?.Tag ?? "");

    public string? ZipBindingRefusal(string zipPath, string id, string version) => PluginZipBinding.Refusal(zipPath, id, version);
}

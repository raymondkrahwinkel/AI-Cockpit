using Cockpit.Core.Plugins;

namespace Cockpit.Core.Abstractions.Plugins;

public interface IPluginSignatureVerifier
{
    PluginSignatureCheck Verify(byte[] statement, string armored);
    bool IsValidIdentity(string id, string version);
    byte[] Statement(string organization, string id, string version, string platform, string sha256, PluginStoreSource? source);
    string? ZipBindingRefusal(string zipPath, string id, string version);
}

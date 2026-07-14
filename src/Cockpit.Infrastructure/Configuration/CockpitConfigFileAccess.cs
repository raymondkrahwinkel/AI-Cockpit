using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Cockpit.Core.Secrets;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>
/// Shared read-modify-write access to the single <c>cockpit.json</c> file. Both the profile store
/// and the notification store go through this so each can update its own section without clobbering
/// the other's: they always load the full <see cref="CockpitConfigFile"/>, mutate one section, and
/// write the whole file back.
/// <para>
/// It is also where the credentials are encrypted and decrypted. Every section — profiles, MCP servers,
/// notifications, and the plugins' own storage — passes through here, so hanging the protection under this one
/// seam covers all of them, and covers a plugin that has never heard of it. Encryption is off unless the
/// operator turned it on and unlocked the app, in which case <see cref="ISecretKeyHolder.Protector"/> holds the
/// key for as long as the process runs.
/// </para>
/// </summary>
internal sealed class CockpitConfigFileAccess(string configFilePath, ISecretKeyHolder? keyHolder = null)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ISecretKeyHolder _keyHolder = keyHolder ?? SecretKeyHolder.Shared;

    public string ConfigFilePath => configFilePath;

    public async Task<CockpitConfigFile?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(configFilePath))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(configFilePath, cancellationToken).ConfigureAwait(false);
        var document = JsonNode.Parse(json);
        if (document is null)
        {
            return null;
        }

        if (_keyHolder.Protector is { } protector)
        {
            SecretJsonWalker.Transform(document, _keyHolder.Fields, (path, value) =>
                SecretProtector.IsProtected(value) ? protector.Unprotect(path, value) : null);
        }

        return document.Deserialize<CockpitConfigFile>(SerializerOptions);
    }

    /// <summary>
    /// Loads the current file (or a fresh, empty one), applies <paramref name="mutate"/> to a single
    /// section, and writes the whole document back — preserving every other section.
    /// </summary>
    public async Task UpdateAsync(Action<CockpitConfigFile> mutate, CancellationToken cancellationToken)
    {
        var configFile = await ReadAsync(cancellationToken).ConfigureAwait(false) ?? new CockpitConfigFile();
        mutate(configFile);

        var document = JsonSerializer.SerializeToNode(configFile, SerializerOptions)
            ?? throw new InvalidOperationException("The cockpit configuration serialized to nothing.");

        if (_keyHolder.Protector is { } protector)
        {
            SecretJsonWalker.Transform(document, _keyHolder.Fields, (path, value) => protector.Protect(path, value));
        }

        // Owner-only: this file holds provider API keys, MCP bearer headers and the plugins' tokens. A plain
        // File.Create leaves it at the umask, which on a stock Fedora means every account on the machine can
        // read them. CockpitConfigPath owns that rule for every credential-bearing file the cockpit writes.
        await using var stream = CockpitConfigPath.CreatePrivateFile(configFilePath);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(document.ToJsonString(SerializerOptions)), cancellationToken)
            .ConfigureAwait(false);
    }
}

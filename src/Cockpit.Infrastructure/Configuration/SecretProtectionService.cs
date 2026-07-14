using System.Text.Json;
using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Secrets;
using Cockpit.Core.Secrets;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>
/// Turns credential encryption on and off, and unlocks it at startup.
/// <para>
/// It works on the raw JSON rather than the typed config model on purpose: a migration must convert every
/// credential that is <em>in the file</em>, including the sections of plugins this build has never heard of and
/// any field a future version adds. Round-tripping through the typed model would silently drop what it does not
/// know about — and dropping a section during the one operation that rewrites every credential is exactly the
/// data loss this is supposed to prevent.
/// </para>
/// </summary>
internal sealed class SecretProtectionService : ISecretProtectionService, ISingletonService
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _configFilePath;
    private readonly SecretKeyHolder _keyHolder;

    public SecretProtectionService()
        : this(CockpitConfigPath.Default, SecretKeyHolder.Shared)
    {
    }

    /// <summary>Test seam: an arbitrary config file and a holder that is not the process-wide one.</summary>
    internal SecretProtectionService(string configFilePath, SecretKeyHolder keyHolder)
    {
        _configFilePath = configFilePath;
        _keyHolder = keyHolder;
    }

    public async Task<SecretProtectionStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var security = await ReadSecurityAsync(cancellationToken).ConfigureAwait(false);

        return new SecretProtectionStatus(security?.Enabled ?? false, _keyHolder.Protector is not null);
    }

    public async Task<bool> UnlockAsync(string password, CancellationToken cancellationToken = default)
    {
        var security = await ReadSecurityAsync(cancellationToken).ConfigureAwait(false);
        if (security is not { Enabled: true })
        {
            return true;
        }

        var protector = ProtectorFor(password, security);
        try
        {
            // The verifier is a known string: decrypting it proves the key, without having to read — and risk
            // mangling — the operator's actual credentials to find out whether the password was right.
            if (protector.Unprotect(SecretProtectionEntry.VerifierPath, security.Verifier)
                != SecretProtectionEntry.VerifierPlaintext)
            {
                return false;
            }
        }
        catch (SecretProtectionException)
        {
            return false;
        }

        _keyHolder.Unlock(protector);

        return true;
    }

    public async Task EnableAsync(
        string password,
        IProgress<SecretMigrationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var document = await ReadDocumentAsync(cancellationToken).ConfigureAwait(false);
        var security = new SecretProtectionEntry
        {
            Enabled = true,
            Salt = Convert.ToBase64String(SecretKey.NewSalt()),
            Kdf = SecretKey.Pbkdf2Sha512,
            Iterations = SecretKey.DefaultIterations,
        };

        var protector = ProtectorFor(password, security);
        security.Verifier = protector.Protect(SecretProtectionEntry.VerifierPath, SecretProtectionEntry.VerifierPlaintext);

        // Encrypt what is there now — a value already encrypted (a half-migrated file from an interrupted run)
        // is left alone by the protector rather than encrypted twice.
        ConvertSecrets(document, progress, (path, value) => protector.Protect(path, value));
        WriteSecurity(document, security);

        Save(document);
        _keyHolder.Unlock(protector);
    }

    public async Task DisableAsync(
        IProgress<SecretMigrationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (_keyHolder.Protector is not { } protector)
        {
            throw new InvalidOperationException("The cockpit must be unlocked before encryption can be turned off.");
        }

        var document = await ReadDocumentAsync(cancellationToken).ConfigureAwait(false);

        ConvertSecrets(document, progress, (path, value) =>
            SecretProtector.IsProtected(value) ? protector.Unprotect(path, value) : null);
        WriteSecurity(document, security: null);

        Save(document);
        _keyHolder.Lock();
    }

    public async Task ChangePasswordAsync(
        string currentPassword,
        string newPassword,
        IProgress<SecretMigrationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!await UnlockAsync(currentPassword, cancellationToken).ConfigureAwait(false))
        {
            throw new SecretProtectionException("The current password is not correct.");
        }

        await DisableAsync(progress: null, cancellationToken).ConfigureAwait(false);
        await EnableAsync(newPassword, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task ResetForgottenPasswordAsync(CancellationToken cancellationToken = default)
    {
        var document = await ReadDocumentAsync(cancellationToken).ConfigureAwait(false);

        // Every credential goes; everything else the operator configured stays. The backup the atomic write keeps
        // still holds the ciphertext, so this is not quite the end of the world if the password resurfaces.
        SecretJsonWalker.Transform(document, _keyHolder.Fields, (_, _) => string.Empty);
        WriteSecurity(document, security: null);

        Save(document);
        _keyHolder.Lock();
    }

    /// <summary>
    /// Rewrites every credential in the document, reporting progress. All-or-nothing: one field that will not
    /// convert (a wrong key, an altered value) aborts the whole migration with the file untouched, rather than
    /// leaving a config half in the clear and half unreadable with no way to tell which fields are which.
    /// </summary>
    private void ConvertSecrets(JsonNode document, IProgress<SecretMigrationProgress>? progress, Func<string, string, string?> transform)
    {
        // Counting pass first: a progress bar that does not know its total cannot show progress. It runs on a
        // clone and writes each value back as it found it, so it counts the credential fields without touching
        // the document the migration is about to rewrite.
        var total = SecretJsonWalker.Transform(document.DeepClone(), _keyHolder.Fields, (_, value) => value).Count;

        var completed = 0;
        progress?.Report(new SecretMigrationProgress(completed, total));

        SecretJsonWalker.Transform(document, _keyHolder.Fields, (path, value) =>
        {
            var converted = transform(path, value);
            progress?.Report(new SecretMigrationProgress(++completed, total));

            return converted;
        });
    }

    private SecretProtector ProtectorFor(string password, SecretProtectionEntry security) =>
        new(SecretKey.Derive(password, Convert.FromBase64String(security.Salt), security.Iterations, security.Kdf));

    private async Task<JsonNode> ReadDocumentAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_configFilePath))
        {
            return new JsonObject();
        }

        var json = await File.ReadAllTextAsync(_configFilePath, cancellationToken).ConfigureAwait(false);

        return JsonNode.Parse(json) ?? new JsonObject();
    }

    private async Task<SecretProtectionEntry?> ReadSecurityAsync(CancellationToken cancellationToken)
    {
        var document = await ReadDocumentAsync(cancellationToken).ConfigureAwait(false);

        return document["Security"]?.Deserialize<SecretProtectionEntry>(SerializerOptions);
    }

    private static void WriteSecurity(JsonNode document, SecretProtectionEntry? security)
    {
        if (document is not JsonObject json)
        {
            return;
        }

        if (security is null)
        {
            json.Remove("Security");

            return;
        }

        json["Security"] = JsonSerializer.SerializeToNode(security, SerializerOptions);
    }

    private void Save(JsonNode document) =>
        CockpitConfigPath.ReplaceAtomicallyPrivate(_configFilePath, document.ToJsonString(SerializerOptions));
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Secrets;
using Cockpit.Core.Secrets;

namespace Cockpit.Infrastructure.Configuration;

// Turns credential encryption on/off and unlocks it at startup. Works on the raw JSON, not the typed
// model, so a migration converts every credential, including plugin sections this build never heard of.
// Every rewrite takes the shared `CockpitConfigWriteGate`, exactly once (non-reentrant).
internal sealed class SecretProtectionService : ISecretProtectionService, ISingletonService
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private const string BackupSuffix = ".bak";

    private readonly string _configFilePath;
    private readonly SecretKeyHolder _keyHolder;

    // Builds the protector for a derived key. A seam, not a constant, so a test can inject a protector whose
    // ciphertext does not decrypt back — which is the only way to exercise the verify-before-publish abort, since
    // the real AES-GCM protector always round-trips.
    private readonly Func<byte[], ISecretProtector> _protectorFactory;

    public SecretProtectionService()
        : this(CockpitConfigPath.Default, SecretKeyHolder.Shared)
    {
    }

    // Test seam: an arbitrary config file, a holder that is not the process-wide one, and — for the
    // round-trip-failure tests — a protector factory that stands in for the real AES-GCM one.
    internal SecretProtectionService(
        string configFilePath,
        SecretKeyHolder keyHolder,
        Func<byte[], ISecretProtector>? protectorFactory = null)
    {
        _configFilePath = configFilePath;
        _keyHolder = keyHolder;
        _protectorFactory = protectorFactory ?? (key => new SecretProtector(key));
    }

    public async Task<SecretProtectionStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var document = await ReadDocumentAsync(cancellationToken).ConfigureAwait(false);
        var security = document["Security"]?.Deserialize<SecretProtectionEntry>(SerializerOptions);
        var enabled = security?.Enabled ?? false;
        var unlocked = _keyHolder.Protector is not null;

        // Review #7 (2026-07-19): banner shows only while encryption is off and at least one credential
        // field is not already dismissed — keyed on additions, so removing/rotating an existing one does
        // not re-nag; dismissal is a subset check, not equality.
        var shouldWarn = false;
        if (!enabled)
        {
            var plaintextPaths = PlaintextSecretPaths(document);
            if (plaintextPaths.Count > 0)
            {
                var dismissed = document["SecurityNotice"]?.Deserialize<SecurityNoticeEntry>(SerializerOptions)?.DismissedPaths;
                var dismissedSet = dismissed is null ? null : new HashSet<string>(dismissed, StringComparer.Ordinal);
                shouldWarn = dismissedSet is null || plaintextPaths.Any(path => !dismissedSet.Contains(path));
            }
        }

        return new SecretProtectionStatus(enabled, unlocked, shouldWarn, HelloEnabled: enabled && security?.Hello is not null, HelloInSoftware: security?.Hello?.InSoftware ?? false,
            HelloKeyName: security?.Hello?.CredentialName);
    }

    public async Task<bool> UnlockAsync(string password, CancellationToken cancellationToken = default)
    {
        var security = await ReadSecurityAsync(cancellationToken).ConfigureAwait(false);
        if (security is not { Enabled: true })
        {
            return true;
        }

        var protector = ProtectorFor(password, security);
        if (!VerifierMatches(protector, security))
        {
            return false;
        }

        _keyHolder.Unlock(protector);

        // Startup sweep (AC-41, Raymond 2026-07-19): a crash after a migration's Save but before its own scrub, or
        // an old recovery quarantine, can leave plaintext credentials in a sidecar while the live config is
        // ciphertext. This is the first moment the key is in hand on a normal start, so it is where they get closed.
        await ScrubPlaintextSidecarsAsync(protector, cancellationToken).ConfigureAwait(false);

        return true;
    }

    public async Task EnableAsync(
        string password,
        IProgress<SecretMigrationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var gate = await CockpitConfigWriteGate.AcquireAsync(_configFilePath, cancellationToken).ConfigureAwait(false);

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

        // A value already encrypted (a half-migrated file from an interrupted run) is left alone rather
        // than double-encrypted. Each freshly encrypted field is proven to read back before the atomic
        // swap wipes the plaintext — a field that won't round-trip aborts here, file untouched.
        ConvertSecrets(document, progress, (path, value) =>
            SecretProtector.IsProtected(value) ? value : ProtectVerified(protector, path, value));
        WriteSecurity(document, security);

        // Encryption on answers the banner; clear any dismissal so a later Disable starts from a clean slate.
        WriteSecurityNotice(document, notice: null);

        Save(document);
        _keyHolder.Unlock(protector);

        // The plaintext the atomic swap just replaced still lives in the .bak it kept, and in any .damaged-* copy
        // an earlier recovery quarantined. Scrub both now that the key is in hand — otherwise the at-rest plaintext
        // this whole feature exists to remove would simply move next door.
        ScrubPlaintextSidecars(protector);
    }

    public async Task DisableAsync(
        IProgress<SecretMigrationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (_keyHolder.Protector is not { } protector)
        {
            throw new InvalidOperationException("The cockpit must be unlocked before encryption can be turned off.");
        }

        using var gate = await CockpitConfigWriteGate.AcquireAsync(_configFilePath, cancellationToken).ConfigureAwait(false);

        var document = await ReadDocumentAsync(cancellationToken).ConfigureAwait(false);

        ConvertSecrets(document, progress, (path, value) =>
            SecretProtector.IsProtected(value) ? protector.Unprotect(path, value) : null);
        WriteSecurity(document, security: null);

        // A deliberate Disable exposes every credential again, so the banner should return at once (Raymond,
        // 2026-07-19): clear the dismissal for the set now back in the clear.
        WriteSecurityNotice(document, notice: null);

        Save(document);
        _keyHolder.Lock();
    }

    public async Task ChangePasswordAsync(
        string currentPassword,
        string newPassword,
        IProgress<SecretMigrationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Review #1: one gated pass, not Disable-then-Enable — that would deadlock the non-reentrant gate
        // and briefly write every credential to disk in the clear. Decrypt-then-re-encrypt happens entirely
        // in memory; the file goes straight from old ciphertext to new, never readable in between.
        using var gate = await CockpitConfigWriteGate.AcquireAsync(_configFilePath, cancellationToken).ConfigureAwait(false);

        var document = await ReadDocumentAsync(cancellationToken).ConfigureAwait(false);
        var current = document["Security"]?.Deserialize<SecretProtectionEntry>(SerializerOptions);
        if (current is not { Enabled: true })
        {
            throw new SecretProtectionException("Encryption is not on, so there is no password to change.");
        }

        var currentProtector = ProtectorFor(currentPassword, current);
        if (!VerifierMatches(currentProtector, current))
        {
            throw new SecretProtectionException("The current password is not correct.");
        }

        var next = new SecretProtectionEntry
        {
            Enabled = true,
            Salt = Convert.ToBase64String(SecretKey.NewSalt()),
            Kdf = SecretKey.Pbkdf2Sha512,
            Iterations = SecretKey.DefaultIterations,
        };
        var nextProtector = ProtectorFor(newPassword, next);
        next.Verifier = nextProtector.Protect(SecretProtectionEntry.VerifierPath, SecretProtectionEntry.VerifierPlaintext);

        // Decrypt each field with the old key (proving it, since a wrong current password should already have been
        // caught above) and re-encrypt with the new one, verified — a plain field from a half-migrated file is
        // simply encrypted. The plaintext exists only as a local here; it never reaches the document that is saved.
        ConvertSecrets(document, progress, (path, value) =>
        {
            var plaintext = SecretProtector.IsProtected(value) ? currentProtector.Unprotect(path, value) : value;

            return ProtectVerified(nextProtector, path, plaintext);
        });
        WriteSecurity(document, next);

        Save(document);
        _keyHolder.Unlock(nextProtector);
        ScrubPlaintextSidecars(nextProtector);
    }

    public async Task ResetForgottenPasswordAsync(CancellationToken cancellationToken = default)
    {
        using var gate = await CockpitConfigWriteGate.AcquireAsync(_configFilePath, cancellationToken).ConfigureAwait(false);

        var document = await ReadDocumentAsync(cancellationToken).ConfigureAwait(false);

        // Every credential goes; everything else the operator configured stays. The backup the atomic write keeps
        // still holds the ciphertext, so this is not quite the end of the world if the password resurfaces.
        SecretJsonWalker.Transform(document, _keyHolder.Fields, (_, _) => string.Empty);
        WriteSecurity(document, security: null);
        WriteSecurityNotice(document, notice: null);

        Save(document);
        _keyHolder.Lock();
    }

    public async Task DismissUnprotectedWarningAsync(CancellationToken cancellationToken = default)
    {
        using var gate = await CockpitConfigWriteGate.AcquireAsync(_configFilePath, cancellationToken).ConfigureAwait(false);

        var document = await ReadDocumentAsync(cancellationToken).ConfigureAwait(false);

        // Nothing to silence once encryption is on — and re-reading under the gate is what keeps a dismissal from
        // landing on top of a set of credentials that changed between the banner showing and the click.
        if (document["Security"]?.Deserialize<SecretProtectionEntry>(SerializerOptions)?.Enabled == true)
        {
            return;
        }

        var plaintextPaths = PlaintextSecretPaths(document);
        if (plaintextPaths.Count == 0)
        {
            return;
        }

        // Store the field paths themselves, sorted, so a later status can tell a genuinely new credential (a path
        // not in this set) from a removal or a rotation of one already here (review #7). The paths are field
        // locations, not values, so they are safe to keep in the clear while encryption is off.
        WriteSecurityNotice(document, new SecurityNoticeEntry
        {
            DismissedPaths = [.. plaintextPaths.Distinct().OrderBy(path => path, StringComparer.Ordinal)],
        });
        Save(document);
    }

    public async Task<bool> EnableHelloAsync(string password, ISecretKeySigner signer, CancellationToken cancellationToken = default)
    {
        var security = await ReadSecurityAsync(cancellationToken).ConfigureAwait(false);
        if (security is not { Enabled: true })
        {
            throw new SecretProtectionException("Encryption is not on, so there is nothing for Windows Hello to unlock.");
        }

        var key = SecretKey.Derive(password, Convert.FromBase64String(security.Salt), security.Iterations, security.Kdf);
        try
        {
            if (!VerifierMatches(_protectorFactory(key), security))
            {
                throw new SecretProtectionException("That password does not open this configuration.");
            }

            // The prompts run before the write gate is taken, so a slow gesture never holds up other settings writes.
            var name = HelloCredentialName(_configFilePath);
            // One prompt covers one signature (measured: a second signature asked again), so whether this Windows signs
            // the same each time is settled at the first unlock rather than here (AC-1879, Raymond 31-10).
            var challenge = RandomNumberGenerator.GetBytes(32);
            if (await signer.EnrollAsync(name, challenge, cancellationToken).ConfigureAwait(false) is not { } enrollment)
            {
                await signer.DeleteAsync(name, cancellationToken).ConfigureAwait(false);
                return false;
            }

            var (storage, first) = (enrollment.Storage, enrollment.Signature);
            var kek = HelloKek(first, challenge);
            var hello = new HelloEntry
            {
                CredentialName = name,
                Challenge = Convert.ToBase64String(challenge),
                WrappedKey = new SecretProtector(kek).Protect(HelloEntry.WrappedKeyPath, Convert.ToBase64String(key)),
                InSoftware = storage is SecretKeyStorage.Software,
            };
            CryptographicOperations.ZeroMemory(kek);
            CryptographicOperations.ZeroMemory(first);

            using var gate = await CockpitConfigWriteGate.AcquireAsync(_configFilePath, cancellationToken).ConfigureAwait(false);
            var document = await ReadDocumentAsync(cancellationToken).ConfigureAwait(false);
            var current = document["Security"]?.Deserialize<SecretProtectionEntry>(SerializerOptions);

            // The password changed while the prompt was up: this wrap holds the old key, so it is not stored.
            if (current is not { Enabled: true } || current.Salt != security.Salt)
            {
                await signer.DeleteAsync(name, cancellationToken).ConfigureAwait(false);
                throw new SecretProtectionException("The password changed meanwhile; turn Windows Hello on again.");
            }

            current.Hello = hello;
            WriteSecurity(document, current);
            Save(document);

            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public async Task<HelloUnlockResult> UnlockWithHelloAsync(ISecretKeySigner signer, CancellationToken cancellationToken = default)
    {
        var security = await ReadSecurityAsync(cancellationToken).ConfigureAwait(false);
        if (security is not { Enabled: true, Hello: { } hello } || !await signer.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            return HelloUnlockResult.NotOffered;
        }

        if (!await signer.ExistsAsync(hello.CredentialName, cancellationToken).ConfigureAwait(false))
        {
            return HelloUnlockResult.KeyGone;
        }

        var challenge = Convert.FromBase64String(hello.Challenge);
        var signature = await signer.SignAsync(hello.CredentialName, challenge, cancellationToken).ConfigureAwait(false);
        if (signature is null)
        {
            return HelloUnlockResult.Cancelled;
        }

        byte[] key;
        var kek = HelloKek(signature, challenge);
        CryptographicOperations.ZeroMemory(signature);
        try
        {
            key = Convert.FromBase64String(new SecretProtector(kek).Unprotect(HelloEntry.WrappedKeyPath, hello.WrappedKey));
        }
        catch (Exception exception) when (exception is SecretProtectionException or FormatException)
        {
            // Never unlocked before: this Windows signs differently each time (RSA-PSS), so Hello can never work here.
            if (!hello.Proven)
            {
                await DisableHelloAsync(signer, cancellationToken).ConfigureAwait(false);
                return HelloUnlockResult.Unsupported;
            }

            // Another machine's TPM, a re-created Hello key, or a signature that changed after an update: the password still works.
            return HelloUnlockResult.KeyGone;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }

        // Not zeroed: the protector keeps this array as the key for the rest of the run.
        var protector = _protectorFactory(key);

        // A wrap of a key from before a password change unwraps fine but no longer opens the verifier.
        if (!VerifierMatches(protector, security))
        {
            return HelloUnlockResult.KeyGone;
        }

        _keyHolder.Unlock(protector);
        await ScrubPlaintextSidecarsAsync(protector, cancellationToken).ConfigureAwait(false);
        if (!hello.Proven)
        {
            await _MarkHelloProvenAsync(hello.WrappedKey, cancellationToken).ConfigureAwait(false);
        }

        return HelloUnlockResult.Unlocked;
    }

    // Only the wrap that just worked is marked: one re-made meanwhile has yet to prove itself.
    private async Task _MarkHelloProvenAsync(string wrappedKey, CancellationToken cancellationToken)
    {
        using var gate = await CockpitConfigWriteGate.AcquireAsync(_configFilePath, cancellationToken).ConfigureAwait(false);
        var document = await ReadDocumentAsync(cancellationToken).ConfigureAwait(false);
        if (document["Security"]?.Deserialize<SecretProtectionEntry>(SerializerOptions) is { Hello: { } current } security
            && current.WrappedKey == wrappedKey)
        {
            current.Proven = true;
            WriteSecurity(document, security);
            Save(document);
        }
    }

    public async Task DisableHelloAsync(ISecretKeySigner signer, CancellationToken cancellationToken = default)
    {
        using (var gate = await CockpitConfigWriteGate.AcquireAsync(_configFilePath, cancellationToken).ConfigureAwait(false))
        {
            var document = await ReadDocumentAsync(cancellationToken).ConfigureAwait(false);
            if (document["Security"]?.Deserialize<SecretProtectionEntry>(SerializerOptions) is { Hello: not null } security)
            {
                security.Hello = null;
                WriteSecurity(document, security);
                Save(document);
            }
        }

        await signer.DeleteAsync(HelloCredentialName(_configFilePath), cancellationToken).ConfigureAwait(false);
    }

    // One OS key per state root, so a second cockpit (another state root) on the same account cannot overwrite it.
    // No "/" or "\": Windows refuses such a name with NTE_INVALID_PARAMETER before any prompt (measured, AC-1879).
    internal static string HelloCredentialName(string configFilePath) =>
        "AI-Cockpit-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(configFilePath).ToUpperInvariant())))[..16];

    private static byte[] HelloKek(byte[] signature, byte[] challenge) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, signature, 32, challenge, "cockpit-hello-v1"u8.ToArray());

    // Rewrites every credential in the document, reporting progress. All-or-nothing: one field that will not
    // convert (a wrong key, an altered value) aborts the whole migration with the file untouched, rather than
    // leaving a config half in the clear and half unreadable with no way to tell which fields are which.
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

    // The credential fields whose value is still in the clear — the set the awareness banner is about. Runs on a
    // clone because the walker rewrites in place and this only wants to read the shape; a value already encrypted
    // is not counted, so a half-migrated file does not keep the banner up over ciphertext.
    private IReadOnlyList<string> PlaintextSecretPaths(JsonNode document) =>
        SecretJsonWalker.Transform(
            document.DeepClone(),
            _keyHolder.Fields,
            (_, value) => SecretProtector.IsProtected(value) ? null : value);

    // Encrypts `plaintext` and proves it reads back in the same field before returning the
    // ciphertext — the verify-before-publish guard. A protector whose ciphertext will not decrypt to what it just
    // encrypted throws here, before anything is saved, so the on-disk file is left exactly as it was.
    private static string ProtectVerified(ISecretProtector protector, string path, string plaintext)
    {
        var ciphertext = protector.Protect(path, plaintext);
        if (protector.Unprotect(path, ciphertext) != plaintext)
        {
            throw new SecretProtectionException(
                $"The credential at '{path}' did not survive a round-trip through encryption; your settings were left untouched.");
        }

        return ciphertext;
    }

    // Whether `protector` is built from the right password: decrypting the known verifier string
    // proves the key without touching — and risking mangling — the operator's actual credentials. A value that
    // will not decrypt (the wrong password) is a false, not a throw.
    internal static bool VerifierMatches(ISecretProtector protector, SecretProtectionEntry security)
    {
        try
        {
            return protector.Unprotect(SecretProtectionEntry.VerifierPath, security.Verifier)
                == SecretProtectionEntry.VerifierPlaintext;
        }
        catch (SecretProtectionException)
        {
            return false;
        }
    }

    private ISecretProtector ProtectorFor(string password, SecretProtectionEntry security) =>
        _protectorFactory(SecretKey.Derive(password, Convert.FromBase64String(security.Salt), security.Iterations, security.Kdf));

    private async Task<JsonNode> ReadDocumentAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_configFilePath))
        {
            return new JsonObject();
        }

        // Review #9: the same retry-read the settings stores use — waiting a writer out mid-swap, rather
        // than reading ungated, is what keeps a status probe from throwing (the 2026-07-15 fix, applied here).
        var json = await CockpitConfigFileAccess.ReadWhenNotBeingReplacedAsync(_configFilePath, cancellationToken).ConfigureAwait(false);

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

    private static void WriteSecurityNotice(JsonNode document, SecurityNoticeEntry? notice)
    {
        if (document is not JsonObject json)
        {
            return;
        }

        if (notice?.DismissedPaths is not { Count: > 0 })
        {
            json.Remove("SecurityNotice");

            return;
        }

        json["SecurityNotice"] = JsonSerializer.SerializeToNode(notice, SerializerOptions);
    }

    private async Task ScrubPlaintextSidecarsAsync(ISecretProtector protector, CancellationToken cancellationToken)
    {
        using var gate = await CockpitConfigWriteGate.AcquireAsync(_configFilePath, cancellationToken).ConfigureAwait(false);

        ScrubPlaintextSidecars(protector);
    }

    // Re-encrypts any plaintext credential left in a sidecar, or removes a copy too damaged to read. Assumes the
    // write gate is already held (its callers take it), so it never races the atomic swap that also writes .bak.
    private void ScrubPlaintextSidecars(ISecretProtector protector)
    {
        // The .bak the atomic swap keeps is always valid JSON — a copy of a good config — so re-encrypt it in place.
        ScrubSidecar(_configFilePath + BackupSuffix, protector, deleteIfUnreadable: false);

        // Older recovery quarantines (cockpit.json.damaged-<timestamp>) can hold plaintext too (Raymond,
        // 2026-07-19). Re-encrypt what parses; delete what does not — a copy we cannot even read is worthless as
        // recovery and is only somewhere for a plaintext token to sit.
        var directory = Path.GetDirectoryName(_configFilePath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return;
        }

        foreach (var damaged in Directory.EnumerateFiles(directory, $"{Path.GetFileName(_configFilePath)}.damaged-*"))
        {
            ScrubSidecar(damaged, protector, deleteIfUnreadable: true);
        }
    }

    private void ScrubSidecar(string path, ISecretProtector protector, bool deleteIfUnreadable)
    {
        if (!File.Exists(path))
        {
            return;
        }

        JsonNode? document;
        try
        {
            document = JsonNode.Parse(File.ReadAllText(path));
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            document = null;
        }

        if (document is null)
        {
            if (deleteIfUnreadable)
            {
                TryDelete(path);
            }

            return;
        }

        var rewritten = SecretJsonWalker.Transform(document, _keyHolder.Fields,
            (fieldPath, value) => SecretProtector.IsProtected(value) ? null : protector.Protect(fieldPath, value));

        if (rewritten.Count > 0)
        {
            CockpitConfigPath.WriteAllTextPrivate(path, document.ToJsonString(SerializerOptions));
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A locked leftover is untidy, not dangerous — the next start sweeps it.
        }
    }

    private void Save(JsonNode document) =>
        CockpitConfigPath.ReplaceAtomicallyPrivate(_configFilePath, document.ToJsonString(SerializerOptions));
}

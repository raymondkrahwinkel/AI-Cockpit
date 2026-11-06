using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Plugins;

namespace Cockpit.Core.Plugins;

// AC-510[b]: Provisioning seam over IPluginStoreClient/IPluginInstaller — download-verify-install glue
// moved out of PluginManagerViewModel. Consent walk and registration re-pin stay with the caller, since
// both need UI/session state this service shouldn't hold. (Omitted: screen-less-caller rationale; see ticket.)
public sealed class PluginProvisioningService(IPluginStoreClient storeClient, IPluginInstaller installer, IPluginSignatureVerifier signatures, IPluginTrustStore trust)
    : IPluginProvisioningService, ISingletonService
{
    public async Task<PluginProvisionResult> InstallAsync(
        PluginProvisionRequest request, int hostAbstractionsMajor, Version? hostVersion = null, CancellationToken cancellationToken = default, IProgress<PluginDownloadProgress>? progress = null,
        Func<PluginKeyConsentRequest, CancellationToken, Task<bool>>? keyConsent = null)
    {
        var effectiveHostVersion = hostVersion ?? HostVersionInfo.Current;
        var folderId = PluginFolderName.Normalize(request.Id);
        var previous = await trust.GetAsync(folderId, cancellationToken).ConfigureAwait(false) ?? request.RestoredTrust;
        var required = request.Store.Signed is not null || previous is not null;
        if (required && !signatures.IsValidIdentity(request.Id, request.Version.Version))
        {
            return _Refuse(request, "The signed plugin identity or version is invalid.");
        }

        // Checked before anything is fetched (AC-181): a version this host cannot run is refused without spending
        // a download on it, and the installer's own gate (a defense-in-depth second check) never gets the chance
        // to be the only thing that caught it. No package for this RID is one of those reasons (AC-1740).
        if (!PluginCompatibility.IsCompatible(request.Version, hostAbstractionsMajor, effectiveHostVersion)
            || request.Version.PackageFor() is not { } package)
        {
            var reason = PluginCompatibility.IncompatibilityReason(request.Version, hostAbstractionsMajor, effectiveHostVersion);
            return new PluginProvisionResult(PluginProvisionOutcome.Incompatible, request.Id, request.Name, reason, Warning: null, FolderId: null, Sha256: null);
        }

        if (request.Store.Signed is not null && (string.IsNullOrEmpty(package.Sha256) || string.IsNullOrEmpty(package.Signature)))
        {
            return _Refuse(request, "A signed source requires a checksum and a publisher signature.");
        }

        var download = request.Store.Signed is null
            ? await storeClient.DownloadZipAsync(request.Store, package.Path, package.Sha256, cancellationToken, progress).ConfigureAwait(false)
            : await storeClient.DownloadZipAsync(request.Store, request.Version, request.Id, cancellationToken, progress).ConfigureAwait(false);
        if (download.ZipPath is null)
        {
            return download.ChecksumMismatch
                ? _ChecksumRefusal(request)
                : _Refuse(request, download.Error ?? "Download failed.", errorIsSafeToShow: false);
        }

        try
        {
            if (download.ChecksumMismatch)
            {
                return _ChecksumRefusal(request);
            }
            if (!download.IsSuccess)
            {
                return _Refuse(request, download.Error ?? "Download failed.", errorIsSafeToShow: false);
            }
            string? fingerprint = null;
            if (request.Store.Signed is { } signed)
            {
                await using var zip = File.OpenRead(download.ZipPath);
                if (zip.Length > 512L * 1024 * 1024)
                {
                    return _Refuse(request, "The signed package exceeds 512 MiB.");
                }
                var actualSha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(zip, cancellationToken).ConfigureAwait(false));
                if (!string.Equals(actualSha256, package.Sha256, StringComparison.Ordinal))
                {
                    return _Refuse(request, "The downloaded package does not match the signed checksum.");
                }
                var statement = signatures.Statement(signed.Organization, request.Id, request.Version.Version,
                    request.Version.Packages is null ? "any" : RuntimeInformation.RuntimeIdentifier, actualSha256, request.Version.Source);
                var check = signatures.Verify(statement, package.Signature ?? "");
                if (!check.Ok || check.Fingerprint is null)
                {
                    return _Refuse(request, $"The publisher signature was rejected ({check.Reason}).");
                }
                fingerprint = check.Fingerprint;
                if (signatures.ZipBindingRefusal(download.ZipPath, request.Id, request.Version.Version) is { } refusal)
                {
                    return _Refuse(request, refusal, errorIsSafeToShow: false);
                }
                if (request.RevokedKeys?.Any(key => string.Equals(key.Fingerprint, fingerprint, StringComparison.Ordinal)) == true)
                {
                    return _Refuse(request, "The publisher key has been revoked. This package cannot be installed or updated.");
                }
            }

            if (required)
            {
                var state = await installer.GetInstalledStateAsync(folderId, cancellationToken).ConfigureAwait(false);
                var source = request.Store.Signed?.SourceKey;
                var decision = previous is not null
                    ? !string.Equals(previous.Source, source, StringComparison.Ordinal) ? PluginTrustDecision.SourceChanged
                    : !string.Equals(previous.KeyFingerprint, fingerprint, StringComparison.Ordinal) ? PluginTrustDecision.KeyChanged
                    : PluginTrustDecision.Known
                    : state.Exists ? PluginTrustDecision.SourceChanged : PluginTrustDecision.FirstInstall;
                if (decision != PluginTrustDecision.Known)
                {
                    var consent = new PluginKeyConsentRequest(request.Id, request.Version.Version, decision,
                        request.Store.Signed?.Organization, Uri.TryCreate(request.Store.Location, UriKind.Absolute, out var depot) ? depot.Authority : null, fingerprint, package.KeyName,
                        request.Version.Source?.Repository, request.Version.Source?.Tag, previous?.KeyFingerprint, previous?.Source);
                    if (keyConsent is null || !await keyConsent(consent, cancellationToken).ConfigureAwait(false))
                    {
                        return _Refuse(request, "Publisher key not confirmed yet.", PluginProvisionOutcome.KeyNotConfirmed);
                    }
                }
                if (state.Exists && !request.AllowDowngrade
                    && (!Version.TryParse(state.Version, out var current) || !Version.TryParse(request.Version.Version, out var incoming) || incoming <= current))
                {
                    return _Refuse(request, "An update must have a newer version unless an older version was explicitly selected or pinned.");
                }
            }

            var installed = await installer.InstallFromZipAsync(download.ZipPath, hostAbstractionsMajor, effectiveHostVersion, cancellationToken,
                expectedId: required ? request.Id : null, expectedVersion: required ? request.Version.Version : null).ConfigureAwait(false);
            if (!installed.IsSuccess)
            {
                return new PluginProvisionResult(PluginProvisionOutcome.Failed, request.Id, request.Name, installed.Error ?? "Install failed.", Warning: null, FolderId: null, Sha256: null);
            }

            if (request.Store.Signed is { } savedSource && fingerprint is not null)
            {
                await trust.SaveAsync(folderId, new PluginTrustEntry(savedSource.SourceKey, fingerprint), cancellationToken).ConfigureAwait(false);
            }
            else if (previous is not null)
            {
                await trust.RemoveAsync(folderId, cancellationToken).ConfigureAwait(false);
            }

            var outcome = installed.Staged ? PluginProvisionOutcome.Staged : PluginProvisionOutcome.Installed;
            return new PluginProvisionResult(outcome, request.Id, request.Name, Error: null, download.Warning, installed.FolderId, installed.Sha256, installed.Manifest, InPlace: installed.InPlace, ConsentReason: installed.ConsentReason, ReplacedSha256: installed.ReplacedSha256);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException or CryptographicException)
        {
            return _Refuse(request, $"The signed package was rejected: {exception.Message}", errorIsSafeToShow: false);
        }
        finally
        {
            _TryDelete(download.ZipPath);
        }
    }

    public async Task<PluginProvisionBatchResult> InstallManyAsync(
        IReadOnlyList<PluginProvisionRequest> requests, int hostAbstractionsMajor, Version? hostVersion = null, CancellationToken cancellationToken = default,
        Func<PluginKeyConsentRequest, CancellationToken, Task<bool>>? keyConsent = null)
    {
        var results = new List<PluginProvisionResult>(requests.Count);
        foreach (var request in requests)
        {
            try
            {
                results.Add(await InstallAsync(request, hostAbstractionsMajor, hostVersion, cancellationToken, keyConsent: keyConsent).ConfigureAwait(false));
            }
            catch (Exception exception)
            {
                // Isolate: one plugin throwing (a network fault mid-download, same as PluginManagerViewModel's
                // batch already tolerates) must not take the rest of the batch down with it.
                results.Add(new PluginProvisionResult(PluginProvisionOutcome.Failed, request.Id, request.Name, exception.Message, Warning: null, FolderId: null, Sha256: null));
            }
        }

        return new PluginProvisionBatchResult(results);
    }

    private static PluginProvisionResult _ChecksumRefusal(PluginProvisionRequest request) =>
        _Refuse(request, $"The download of '{request.Name}' {request.Version.Version} did not match the store's published checksum (SHA-256 mismatch) and was rejected.");

    private static PluginProvisionResult _Refuse(PluginProvisionRequest request, string error, PluginProvisionOutcome outcome = PluginProvisionOutcome.Failed, bool errorIsSafeToShow = true) =>
        new(outcome, request.Id, request.Name, error, Warning: null, FolderId: null, Sha256: null, ErrorIsSafeToShow: errorIsSafeToShow);

    private static void _TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // A leftover temp download is harmless; the OS temp cleaner reclaims it.
        }
    }
}

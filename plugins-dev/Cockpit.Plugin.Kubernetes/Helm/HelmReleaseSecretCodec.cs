using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using k8s.Models;

namespace Cockpit.Plugin.Kubernetes.Helm;

// Helm stores each release as a `helm.sh/release.v1` secret (AC-1061 fase 1): the `release` data key is the
// release JSON, gzipped and base64-encoded a second time on top of the layer `V1Secret.Data` already decodes.
// Unwrapping the rest needs only the BCL (Convert + GZipStream) — no helm binary, no extra package.
internal static class HelmReleaseSecretCodec
{
    public static HelmRelease? TryDecode(V1Secret secret, out string? error)
    {
        var secretName = secret.Metadata?.Name ?? "(unnamed)";
        if (secret.Data is null || !secret.Data.TryGetValue("release", out var helmLayerBytes))
        {
            error = $"Secret \"{secretName}\" has no \"release\" data key — not a Helm release secret.";
            return null;
        }

        try
        {
            var helmLayerText = Encoding.ASCII.GetString(helmLayerBytes);
            var gzipBytes = Convert.FromBase64String(helmLayerText);
            using var gzip = new GZipStream(new MemoryStream(gzipBytes), CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            var json = reader.ReadToEnd();
            if (JsonNode.Parse(json) is not JsonObject release)
            {
                error = $"Secret \"{secretName}\": decoded release payload was not a JSON object.";
                return null;
            }

            error = null;
            return HelmRelease.FromJson(release);
        }
        catch (Exception exception) when (exception is FormatException or InvalidDataException or JsonException)
        {
            // A release secret this plugin didn't write (a hand-edited copy, a differently-versioned Helm release
            // format) must not take the whole helm_list/helm_history call down with it — the caller reports it
            // per-secret and moves on.
            error = $"Secret \"{secretName}\": could not decode the release payload ({exception.Message}).";
            return null;
        }
    }
}

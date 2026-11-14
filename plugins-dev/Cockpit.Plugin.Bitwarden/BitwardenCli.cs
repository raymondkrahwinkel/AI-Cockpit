using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Cockpit.Plugin.Bitwarden;

internal sealed class BitwardenException(string reason) : Exception(reason)
{
    public string Reason { get; } = reason;
}

// Runs the official `bws` CLI. The machine-account access token goes only into the child's environment, never into an argument.
internal sealed class BitwardenCli(Func<string, string?> resolveManagedCliPath)
{
    public const string TokenVariable = "BWS_ACCESS_TOKEN";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public async Task<string> RunAsync(string token, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(resolveManagedCliPath("bws") is { Length: > 0 } managed ? managed : "bws")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment[TokenVariable] = token;

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new BitwardenException("bws CLI missing");
        }
        catch (Win32Exception)
        {
            throw new BitwardenException("bws CLI missing");
        }

        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                if (process.ExitCode == 0)
                {
                    return await output.ConfigureAwait(false);
                }

                throw new BitwardenException(Classify(await error.ConfigureAwait(false)));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                throw new BitwardenException("network");
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }
        }
    }

    // Maps bws's stderr to a short reason; the text itself is never passed on.
    internal static string Classify(string standardError)
    {
        var text = standardError.ToLowerInvariant();
        if (text.Contains("token") || text.Contains("authenticat") || text.Contains("401") || text.Contains("unauthori") || text.Contains("invalid"))
        {
            return "token invalid";
        }

        return text.Contains("network") || text.Contains("dns") || text.Contains("sending request") || text.Contains("timeout") || text.Contains("connect")
            ? "network"
            : "bws failed";
    }

    // Lenient: entries without a usable id or name are skipped; only a response that is not a JSON array is an error.
    internal static List<(string Id, string Name)> ParseProjects(string json) => _Parse(json, "name");

    // A secret list also carries every value. Only id and key are copied out of the parsed document; the value is never read.
    internal static List<(string Id, string Name)> ParseSecrets(string json) => _Parse(json, "key");

    // The value of one `bws secret get` answer.
    internal static string ParseValue(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()!
                : throw new BitwardenException("unexpected response");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new BitwardenException("unexpected response");
        }
    }

    private static List<(string Id, string Name)> _Parse(string json, string nameProperty)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var list = new List<(string, string)>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.Object
                    && element.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                    && element.TryGetProperty(nameProperty, out var name) && name.ValueKind == JsonValueKind.String
                    && id.GetString() is { Length: > 0 } idText && name.GetString() is { Length: > 0 } nameText)
                {
                    list.Add((idText, nameText));
                }
            }

            return list;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new BitwardenException("unexpected response");
        }
    }
}

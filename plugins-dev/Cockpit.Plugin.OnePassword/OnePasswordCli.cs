using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Cockpit.Plugin.OnePassword;

internal sealed class OnePasswordException(string reason) : Exception(reason)
{
    public string Reason { get; } = reason;
}

// Runs the official `op` CLI. The service-account token goes only into the child's environment, never into an argument.
internal sealed class OnePasswordCli(Func<string, string?> resolveManagedCliPath)
{
    public const string TokenVariable = "OP_SERVICE_ACCOUNT_TOKEN";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public async Task<string> RunAsync(string token, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(resolveManagedCliPath("op") is { Length: > 0 } managed ? managed : "op")
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

        // A Connect server in the environment would win over the service account; this process must not see one.
        start.Environment.Remove("OP_CONNECT_HOST");
        start.Environment.Remove("OP_CONNECT_TOKEN");
        start.Environment[TokenVariable] = token;

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new OnePasswordException("op CLI missing");
        }
        catch (Win32Exception)
        {
            throw new OnePasswordException("op CLI missing");
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

                throw new OnePasswordException(Classify(await error.ConfigureAwait(false)));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                throw new OnePasswordException("network");
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }
        }
    }

    // Maps op's stderr to a short reason; the text itself is never passed on.
    internal static string Classify(string standardError)
    {
        var text = standardError.ToLowerInvariant();
        if (text.Contains("token") || text.Contains("authenticat") || text.Contains("401") || text.Contains("unauthori"))
        {
            return "token invalid";
        }

        return text.Contains("network") || text.Contains("dial") || text.Contains("lookup") || text.Contains("timeout") || text.Contains("connect")
            ? "network"
            : "op failed";
    }

    internal static List<(string Id, string Name)> ParseVaults(string json) => _Parse(json, "name");

    internal static List<(string Id, string Name)> ParseItems(string json) => _Parse(json, "title");

    private static List<(string Id, string Name)> _Parse(string json, string nameProperty)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var list = new List<(string, string)>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.TryGetProperty("id", out var id) && element.TryGetProperty(nameProperty, out var name)
                    && id.GetString() is { Length: > 0 } idText && name.GetString() is { Length: > 0 } nameText)
                {
                    list.Add((idText, nameText));
                }
            }

            return list;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new OnePasswordException("unexpected response");
        }
    }
}

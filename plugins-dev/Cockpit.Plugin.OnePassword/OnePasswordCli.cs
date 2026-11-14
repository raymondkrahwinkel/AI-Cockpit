using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.ManagedCli;

namespace Cockpit.Plugin.OnePassword;

internal sealed class OnePasswordException(string reason) : Exception(reason)
{
    public string Reason { get; } = reason;
}

// Runs the official `op` CLI. The service-account token goes only into the child's environment, never into an argument.
internal sealed class OnePasswordCli(ICockpitHost host)
{
    internal async Task<ManagedCliInstallResult?> FindInstalledAsync(CancellationToken cancellationToken)
    {
        foreach (var candidate in _Candidates())
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                var text = await _RunAsync(candidate, string.Empty, ["--version"], cancellationToken).ConfigureAwait(false);
                if (Version.TryParse(text.Trim(), out var version) && version >= OnePasswordManagedCli.MinimumVersion)
                {
                    return ManagedCliInstallResult.Ok(version.ToString(), Path.GetFullPath(candidate));
                }
            }
            catch (OnePasswordException)
            {
            }
        }

        return null;
    }

    private IEnumerable<string> _Candidates()
    {
        if (host.ResolveManagedCliPath("op") is { Length: > 0 } installed)
        {
            yield return installed;
        }

        var fileName = OperatingSystem.IsWindows() ? "op.exe" : "op";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try
            {
                candidate = Path.GetFullPath(Path.Combine(directory.Trim('"'), fileName));
            }
            catch (ArgumentException)
            {
                continue;
            }

            yield return candidate;
        }
    }

    internal async Task<ManagedCliInstallResult> EnsureInstalledAsync(CancellationToken cancellationToken)
    {
        if (await FindInstalledAsync(cancellationToken).ConfigureAwait(false) is { } installed)
        {
            return installed;
        }

        var result = await host.InstallManagedCliAsync("op", cancellationToken).ConfigureAwait(false);
        return result.Success && result.ExecutablePath is { Length: > 0 }
            ? result
            : throw new OnePasswordException(result.Error ?? "Could not install the 1Password CLI.");
    }

    public const string TokenVariable = "OP_SERVICE_ACCOUNT_TOKEN";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public async Task<string> RunAsync(string token, IReadOnlyList<string> arguments, CancellationToken cancellationToken, string? input = null)
    {
        var installed = await EnsureInstalledAsync(cancellationToken).ConfigureAwait(false);
        var executable = installed.ExecutablePath ?? throw new OnePasswordException("op CLI missing");
        return await _RunAsync(executable, token, arguments, cancellationToken, input).ConfigureAwait(false);
    }

    private static async Task<string> _RunAsync(string executable, string token, IReadOnlyList<string> arguments, CancellationToken cancellationToken, string? input = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input is not null,
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
                if (input is not null)
                {
                    await process.StandardInput.WriteAsync(input.AsMemory(), timeout.Token).ConfigureAwait(false);
                    process.StandardInput.Close();
                }

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
        if (text.Contains("403") || text.Contains("forbidden") || text.Contains("permission") || text.Contains("read-only") || text.Contains("read only"))
        {
            return "write permission denied";
        }

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

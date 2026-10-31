using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Cockpit.App.Logging;
using Cockpit.Core.Abstractions.Secrets;

namespace Cockpit.App.Services;

// AC-1879: Windows Hello through Cockpit.Hello.exe beside the app (the app itself has no WinRT, like Cockpit.Notify).
// The prompt is owned by `owner`, so it opens above the cockpit's window instead of behind it. Anything off —
// another OS, no helper, a failed or cancelled prompt — reads as "not available" or null, never a throw.
public sealed class WindowsHelloSigner(Window owner) : ISecretKeySigner
{
    private static readonly string HelperPath = Path.Combine(AppContext.BaseDirectory, "Cockpit.Hello.exe");

    // For the verbs that show no prompt: a helper that does not answer in this time is treated as "no".
    private static readonly TimeSpan NoPromptTimeout = TimeSpan.FromSeconds(15);

    // Read on the UI thread when the signer is made; the window is open by then.
    private readonly string _owner = (owner.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero).ToInt64().ToString(CultureInfo.InvariantCulture);

    // Answered once per run: the OS and its Hello setup do not change while the cockpit is open.
    private static Task<bool>? _available;

    public string? LastFailure { get; private set; }

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        _available ??= _IsAvailableAsync();

    public async Task<SecretKeyStorage> CreateAsync(string name, CancellationToken cancellationToken = default) =>
        _Note("create", await _RunAsync(["create", name, _owner], cancellationToken).ConfigureAwait(false)) switch
        {
            "ok tpm" => SecretKeyStorage.Hardware,
            "ok software" => SecretKeyStorage.Software,
            _ => SecretKeyStorage.None,
        };

    public async Task<bool> ExistsAsync(string name, CancellationToken cancellationToken = default) =>
        _Note("exists", await _RunAsync(["exists", name], cancellationToken, NoPromptTimeout).ConfigureAwait(false)) == "ok";

    public async Task<byte[]?> SignAsync(string name, byte[] challenge, CancellationToken cancellationToken = default)
    {
        var line = await _RunAsync(["sign", name, _owner, Convert.ToBase64String(challenge)], cancellationToken).ConfigureAwait(false);
        _Note("sign", line is { } answer && answer.StartsWith("ok ", StringComparison.Ordinal) ? "ok" : line);
        return line is { } ok && ok.StartsWith("ok ", StringComparison.Ordinal) ? Convert.FromBase64String(ok[3..]) : null;
    }

    public async Task DeleteAsync(string name, CancellationToken cancellationToken = default) =>
        _Note("delete", await _RunAsync(["delete", name], cancellationToken, NoPromptTimeout).ConfigureAwait(false));

    // Every answer that is not a success is logged and kept as the reason the operator is shown. The signature
    // itself never passes through here: `sign` hands only "ok" or the failure.
    private string? _Note(string verb, string? answer)
    {
        if (answer is not null && answer.StartsWith("ok", StringComparison.Ordinal))
        {
            LastFailure = null;
            return answer;
        }

        LastFailure = answer switch
        {
            null => "the Windows Hello helper did not answer",
            "UserCanceled" => "the Windows prompt was cancelled",
            "NotFound" => "the Windows Hello key is not on this computer",
            _ when answer.StartsWith("error ", StringComparison.Ordinal) => $"Windows refused it ({answer[6..]})",
            _ => $"Windows Hello answered {answer}",
        };
        LifecycleLog.Write($"Windows Hello {verb}: {LastFailure}");
        return answer;
    }

    private static async Task<bool> _IsAvailableAsync() =>
        OperatingSystem.IsWindows() && File.Exists(HelperPath)
        && await _RunAsync(["supported"], CancellationToken.None, NoPromptTimeout).ConfigureAwait(false) == "ok";

    private static async Task<string?> _RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(HelperPath))
        {
            return null;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = HelperPath,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }

        if (process is null)
        {
            return null;
        }

        using (process)
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            if (timeout is { } after)
            {
                limit.CancelAfter(after);
            }

            try
            {
                var line = await process.StandardOutput.ReadLineAsync(limit.Token).ConfigureAwait(false);
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
                return line?.Trim();
            }
            catch (OperationCanceledException)
            {
                // Leaving the unlock screen closes the prompt with it; a helper past its time limit counts as "no".
                process.Kill(entireProcessTree: true);
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                return null;
            }
        }
    }
}

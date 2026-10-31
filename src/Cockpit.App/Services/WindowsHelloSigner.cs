using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Cockpit.Core.Abstractions.Secrets;

namespace Cockpit.App.Services;

// AC-1879: Windows Hello through Cockpit.Hello.exe beside the app (the app itself has no WinRT, like Cockpit.Notify).
// The prompt is owned by `owner`, so it opens above the cockpit's window instead of behind it. Anything off —
// another OS, no helper, a failed or cancelled prompt — reads as "not available" or null, never a throw.
public sealed class WindowsHelloSigner(Window owner) : ISecretKeySigner
{
    private static readonly string HelperPath = Path.Combine(AppContext.BaseDirectory, "Cockpit.Hello.exe");

    // Answered once per run: the OS and its Hello setup do not change while the cockpit is open.
    private static Task<bool>? _available;

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        _available ??= _IsAvailableAsync();

    public async Task<bool> CreateAsync(string name, CancellationToken cancellationToken = default) =>
        await _RunAsync(["create", name, _Owner()], cancellationToken).ConfigureAwait(false) == "ok";

    public async Task<byte[]?> SignAsync(string name, byte[] challenge, CancellationToken cancellationToken = default)
    {
        var line = await _RunAsync(["sign", name, _Owner(), Convert.ToBase64String(challenge)], cancellationToken).ConfigureAwait(false);
        return line is { } ok && ok.StartsWith("ok ", StringComparison.Ordinal) ? Convert.FromBase64String(ok[3..]) : null;
    }

    public async Task DeleteAsync(string name, CancellationToken cancellationToken = default) =>
        await _RunAsync(["delete", name], cancellationToken).ConfigureAwait(false);

    private static async Task<bool> _IsAvailableAsync() =>
        OperatingSystem.IsWindows() && File.Exists(HelperPath)
        && await _RunAsync(["supported"], CancellationToken.None).ConfigureAwait(false) == "ok";

    private string _Owner() =>
        (owner.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero).ToInt64().ToString(CultureInfo.InvariantCulture);

    private static async Task<string?> _RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
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
        {
            try
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                return line?.Trim();
            }
            catch (OperationCanceledException)
            {
                // Leaving the unlock screen closes the prompt with it rather than leaving it up on its own.
                process.Kill(entireProcessTree: true);
                throw;
            }
        }
    }
}

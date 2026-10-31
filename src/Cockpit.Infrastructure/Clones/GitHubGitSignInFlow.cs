using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Clones;

// Only a recognized device code leaves gh's output; its arbitrary output may contain a credential.
internal sealed class GitHubGitSignInFlow : ILoginFlow
{
    private static readonly Regex DeviceCode = new(@"(?:One-time code \(([A-Z0-9]{4}-[A-Z0-9]{4})\) copied to clipboard|Unsupported Linux code: ([A-Z0-9]{4}-[A-Z0-9]{4})\b)", RegexOptions.Compiled);
    private static readonly Regex Ansi = new(@"\x1b\[[0-9;]*m", RegexOptions.Compiled);
    private readonly Channel<LoginFlowStep> _steps = Channel.CreateBounded<LoginFlowStep>(new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<LoginFlowResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly bool _withToken;
    private readonly Lock _gate = new();
    private Task _run = Task.CompletedTask;
    private Task? _dispose;
    private bool _submitted;

    public GitHubGitSignInFlow(bool withToken)
    {
        _withToken = withToken;
        if (withToken)
        {
            _steps.Writer.TryWrite(new LoginFlowStep("Paste a GitHub token. It will be stored only on the server.", null, true));
        }
        else
        {
            _run = _RunAsync(null);
        }
    }

    public IAsyncEnumerable<LoginFlowStep> Steps => _steps.Reader.ReadAllAsync();

    public Task<LoginFlowResult> Completion => _completion.Task;

    public Task SubmitAsync(string value, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_withToken || _dispose is not null || _submitted)
            {
                throw new InvalidOperationException("This sign-in is not awaiting a token.");
            }

            if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl))
            {
                throw new ArgumentException("A GitHub token without whitespace is required.", nameof(value));
            }

            _submitted = true;
            _steps.Writer.TryWrite(new LoginFlowStep("Checking the GitHub token on the server.", null, false));
            _run = _RunAsync(value);
            return Task.CompletedTask;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            return new ValueTask(_dispose ??= _DisposeAsync());
        }
    }

    private async Task _DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _run.ConfigureAwait(false);
        _steps.Writer.TryComplete();
        _completion.TrySetResult(new LoginFlowResult(false, "The GitHub sign-in ended."));
        _stop.Dispose();
    }

    internal static string? DeviceCodeIn(string output)
    {
        var match = DeviceCode.Match(Ansi.Replace(output, ""));
        return match.Success ? match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value : null;
    }

    private async Task _RunAsync(string? token)
    {
        try
        {
            var login = await RunCommandAsync(
                ["auth", "login", "--hostname", "github.com", "--git-protocol", "https", _withToken ? "--with-token" : "--web"],
                _stop.Token,
                token ?? "",
                line =>
                {
                    if (!_withToken && DeviceCodeIn(line) is { } code)
                    {
                        _steps.Writer.TryWrite(new LoginFlowStep($"Open GitHub and enter code: {code}", new Uri("https://github.com/login/device"), false));
                    }
                }).ConfigureAwait(false);
            if (login.ExitCode != 0)
            {
                _completion.TrySetResult(new LoginFlowResult(false, "GitHub sign-in failed. Check the credential or start again."));
                return;
            }

            var setup = await RunCommandAsync(["auth", "setup-git", "--hostname", "github.com"], _stop.Token).ConfigureAwait(false);
            _completion.TrySetResult(new LoginFlowResult(setup.ExitCode == 0, setup.ExitCode == 0 ? null : "GitHub signed in, but its git credential helper could not be configured."));
        }
        catch (Exception)
        {
            _completion.TrySetResult(new LoginFlowResult(false, "GitHub sign-in could not complete on the server. Start it again."));
        }
        finally
        {
            token = null;
            _steps.Writer.TryComplete();
        }
    }

    internal static async Task<(int ExitCode, string Output)> RunCommandAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        string? input = null,
        Action<string>? received = null)
    {
        var start = new ProcessStartInfo("gh")
        {
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.Environment["GH_PROMPT_DISABLED"] = "1";
        start.Environment["GH_BROWSER"] = OperatingSystem.IsWindows() ? "cmd /c exit 0" : "/bin/true";
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("GitHub CLI could not start.");
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var stdout = _ReadAsync(process.StandardOutput, received, operation.Token);
        var stderr = _ReadAsync(process.StandardError, received, operation.Token);
        try
        {
            if (input is not null)
            {
                await process.StandardInput.WriteLineAsync(input.AsMemory(), operation.Token).ConfigureAwait(false);
            }

            input = null;
            process.StandardInput.Close();
            await process.WaitForExitAsync(operation.Token).ConfigureAwait(false);
            return (process.ExitCode, await stdout.ConfigureAwait(false) + "\n" + await stderr.ConfigureAwait(false));
        }
        finally
        {
            input = null;
            // Closing a Process handle does not stop its child; a broken stdin must end it too.
            await operation.CancelAsync().ConfigureAwait(false);
            try
            {
                await _StopAsync(process).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Readers are observed before their process streams are disposed; the command's failure wins.
                }
            }
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    private static async Task _StopAsync(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            // sudo relays SIGTERM to agent; killing its monitor first orphans that other user's gh.
            var signalled = kill(process.Id, 15);
            var signalError = Marshal.GetLastPInvokeError();
            if (signalled != 0 && !process.HasExited)
            {
                throw new System.ComponentModel.Win32Exception(signalError);
            }

            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (grace.IsCancellationRequested)
            {
            }
        }

        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
    }

    private static async Task<string> _ReadAsync(StreamReader reader, Action<string>? received, CancellationToken cancellationToken)
    {
        // Login output is discarded immediately; only auth-status needs bounded text for the account name.
        var output = new System.Text.StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            received?.Invoke(line);
            if (received is null && output.Length < 16384)
            {
                output.AppendLine(line[..Math.Min(line.Length, 16384 - output.Length)]);
            }
        }

        return output.ToString();
    }
}

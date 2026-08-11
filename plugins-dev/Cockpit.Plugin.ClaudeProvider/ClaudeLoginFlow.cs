using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.ClaudeProvider;

// `claude auth login`, spawned as a plain subprocess — no pty (empirically verified, AC-713): the CLI's browser
// step and its "paste code here" fallback both work over redirected, non-interactive stdio. Lines stream out as
// `LoginFlowStep`s as they arrive; a line naming a URL carries it as `LinkToOpen`, and the final prompt — which
// the CLI prints without a trailing newline, since it then blocks on stdin — is detected from the still-partial
// buffer and reported as `AwaitsInput`. An unrecognised line still streams through as plain text: a wording
// change in a future CLI version degrades to "show the raw output", never a crash.
internal sealed class ClaudeLoginFlow : ILoginFlow
{
    private static readonly Regex _UrlPattern = new(@"https?://\S+", RegexOptions.Compiled);

    private readonly Process _process;
    private readonly Channel<LoginFlowStep> _steps =
        Channel.CreateUnbounded<LoginFlowStep>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly TaskCompletionSource<LoginFlowResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cts;

    private ClaudeLoginFlow(Process process, CancellationTokenSource cts)
    {
        _process = process;
        _cts = cts;
        _ = _PumpOutputAsync();
    }

    public static ClaudeLoginFlow Start(string configJson, Func<string, string?>? managedResolver, CancellationToken cancellationToken)
    {
        var config = ClaudeProviderConfig.Parse(configJson);
        var executablePath = ClaudeExecutableLocator.Resolve(
            string.IsNullOrWhiteSpace(config.ExecutablePath) ? "claude" : config.ExecutablePath,
            managedResolver);
        var spawnOverride = ClaudeConfigPaths.ResolveSpawnOverride(
            config.ConfigDir,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        var startInfo = new ProcessStartInfo(executablePath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("auth");
        startInfo.ArgumentList.Add("login");

        if (!string.IsNullOrWhiteSpace(spawnOverride))
        {
            startInfo.Environment[ClaudeConfigPaths.EnvironmentVariable] = spawnOverride;
        }

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start '{executablePath}'.");
        return new ClaudeLoginFlow(process, CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
    }

    public IAsyncEnumerable<LoginFlowStep> Steps => _steps.Reader.ReadAllAsync(_cts.Token);

    public Task<LoginFlowResult> Completion => _completion.Task;

    public async Task SubmitAsync(string value, CancellationToken cancellationToken)
    {
        await _process.StandardInput.WriteLineAsync(value.AsMemory(), cancellationToken).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Already gone.
        }

        _process.Dispose();
        _cts.Dispose();
    }

    private async Task _PumpOutputAsync()
    {
        var pending = new StringBuilder();
        var buffer = new char[256];

        try
        {
            var stderrDrain = _process.StandardError.ReadToEndAsync(_cts.Token);

            int read;
            while ((read = await _process.StandardOutput.ReadAsync(buffer, _cts.Token).ConfigureAwait(false)) > 0)
            {
                pending.Append(buffer, 0, read);
                _DrainCompleteLines(pending);

                // Re-checked on every read, not latched after the first match: a wrong/expired pasted code makes
                // the CLI print this same no-trailing-newline prompt a second time, and a latch would leave that
                // retry sitting unflushed in `pending` — the row would show nothing, forever, instead of a second
                // input field.
                if (LooksLikeAwaitsInputPrompt(pending.ToString()))
                {
                    _steps.Writer.TryWrite(new LoginFlowStep(pending.ToString().Trim(), null, AwaitsInput: true));
                    pending.Clear();
                }
            }

            if (pending.Length > 0)
            {
                _EmitLine(pending.ToString());
            }

            await _process.WaitForExitAsync(_cts.Token).ConfigureAwait(false);
            var stderr = await stderrDrain.ConfigureAwait(false);

            _completion.TrySetResult(_process.ExitCode == 0
                ? new LoginFlowResult(Success: true, ErrorMessage: null)
                : new LoginFlowResult(Success: false, ErrorMessage: string.IsNullOrWhiteSpace(stderr)
                    ? $"claude auth login exited with code {_process.ExitCode}."
                    : stderr.Trim()));
        }
        catch (OperationCanceledException)
        {
            _completion.TrySetResult(new LoginFlowResult(Success: false, ErrorMessage: null));
        }
        catch (Exception ex)
        {
            _completion.TrySetResult(new LoginFlowResult(Success: false, ErrorMessage: ex.Message));
        }
        finally
        {
            _steps.Writer.TryComplete();
        }
    }

    private void _DrainCompleteLines(StringBuilder pending)
    {
        while (true)
        {
            var text = pending.ToString();
            var newlineIndex = text.IndexOf('\n');
            if (newlineIndex < 0)
            {
                return;
            }

            _EmitLine(text[..newlineIndex]);
            pending.Remove(0, newlineIndex + 1);
        }
    }

    private void _EmitLine(string line)
    {
        if (ClassifyLine(line) is { } step)
        {
            _steps.Writer.TryWrite(step);
        }
    }

    // Pure parsing, split out for testing without a real `claude` subprocess: a blank line emits nothing, a URL
    // anywhere on the line becomes `LinkToOpen`, everything else streams through as plain text.
    internal static LoginFlowStep? ClassifyLine(string line)
    {
        var trimmed = line.Trim('\r', '\n', ' ');
        if (trimmed.Length == 0)
        {
            return null;
        }

        var urlMatch = _UrlPattern.Match(trimmed);
        var link = urlMatch.Success && Uri.TryCreate(urlMatch.Value, UriKind.Absolute, out var uri) ? uri : null;
        return new LoginFlowStep(trimmed, link, AwaitsInput: false);
    }

    // The CLI's own prompt ("Paste code here if prompted >") arrives with no trailing newline, since it then
    // blocks on stdin — this is what tells the pump loop to stop waiting for one and show an input field instead.
    internal static bool LooksLikeAwaitsInputPrompt(string pendingText) =>
        pendingText.Contains("paste code", StringComparison.OrdinalIgnoreCase);
}

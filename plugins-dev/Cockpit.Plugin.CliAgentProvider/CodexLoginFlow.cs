using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.CliAgentProvider;

// `codex login --device-auth`, spawned as a plain subprocess — OpenAI's own documented headless route (the
// default `codex login` names it itself: "On a remote or headless machine? Use `codex login --device-auth`
// instead."). The subprocess polls the token endpoint itself once the operator has visited the link and entered
// the code elsewhere, so unlike `ClaudeLoginFlow` there is nothing to submit back — `SubmitAsync` is a no-op and
// `Completion` simply follows the process exit. Output lines stream through as `LoginFlowStep`s the same way
// `ClaudeLoginFlow` does; a line naming a URL carries it as `LinkToOpen`.
internal sealed class CodexLoginFlow : ILoginFlow
{
    private static readonly Regex _UrlPattern = new(@"https?://\S+", RegexOptions.Compiled);

    private readonly Process _process;
    private readonly Channel<LoginFlowStep> _steps =
        Channel.CreateUnbounded<LoginFlowStep>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly TaskCompletionSource<LoginFlowResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cts;

    private CodexLoginFlow(Process process, CancellationTokenSource cts)
    {
        _process = process;
        _cts = cts;
        _ = _PumpOutputAsync();
    }

    public static CodexLoginFlow Start(string configJson, Func<string, string?>? managedResolver, CancellationToken cancellationToken)
    {
        var config = JsonSerializer.Deserialize<CliAgentConfig>(configJson, CliAgentConfig.JsonOptions) ?? new CliAgentConfig();
        var executablePath = CliExecutableLocator.Resolve(config.Command, managedResolver);

        var startInfo = new ProcessStartInfo(executablePath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("login");
        startInfo.ArgumentList.Add("--device-auth");

        if (!string.IsNullOrWhiteSpace(config.ConfigDir))
        {
            startInfo.Environment["CODEX_HOME"] = config.ConfigDir;
        }

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start '{executablePath}'.");
        return new CodexLoginFlow(process, CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
    }

    public IAsyncEnumerable<LoginFlowStep> Steps => _steps.Reader.ReadAllAsync(_cts.Token);

    public Task<LoginFlowResult> Completion => _completion.Task;

    // The device-auth flow polls the token endpoint itself once the operator has entered the code elsewhere —
    // there is nothing for the host to write back.
    public Task SubmitAsync(string value, CancellationToken cancellationToken) => Task.CompletedTask;

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
                    ? $"codex login --device-auth exited with code {_process.ExitCode}."
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

    // Pure parsing, split out for testing without a real `codex` subprocess: a blank line emits nothing, a URL
    // anywhere on the line becomes `LinkToOpen`, everything else (including the one-time code line) streams
    // through as plain text.
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
}

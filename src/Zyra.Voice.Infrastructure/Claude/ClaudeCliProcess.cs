using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using Zyra.Voice.Core.Configuration;

namespace Zyra.Voice.Infrastructure.Claude;

/// <summary>
/// Real <see cref="IClaudeCliProcess"/> backed by a spawned <c>claude</c> process running in
/// persistent, multi-turn headless mode:
/// <c>claude -p --input-format stream-json --output-format stream-json --verbose --include-partial-messages</c>.
/// Grounded in https://code.claude.com/docs/en/headless.md ("Stream responses": stream-json
/// output requires --verbose; --include-partial-messages adds token-level deltas) and
/// https://code.claude.com/docs/en/agent-sdk/streaming-vs-single-mode.md (streaming input mode:
/// a persistent process fed one JSON user-message object per stdin line keeps a single
/// multi-turn session alive).
/// </summary>
/// <remarks>
/// F-C1 caveat: this sandbox has no logged-in <c>claude</c> CLI, so this class has never been
/// exercised against a real process here. It is deliberately kept as a thin, mockable seam
/// (<see cref="IClaudeCliProcess"/>) so <c>ClaudeCliSession</c>'s turn-taking logic is unit
/// tested against a fake; the live end-to-end run requires Raymond's logged-in environment.
/// </remarks>
internal sealed class ClaudeCliProcess : IClaudeCliProcess
{
    private readonly Process _process;
    private bool _started;

    public ClaudeCliProcess(IOptions<ZyraVoiceOptions> options)
    {
        var cli = options.Value.Claude;

        var arguments = new List<string>
        {
            "-p",
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            "--verbose",
            "--include-partial-messages",
            "--permission-mode", cli.PermissionMode,
        };
        arguments.AddRange(cli.ExtraArguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = cli.ExecutablePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        if (!string.IsNullOrWhiteSpace(cli.WorkingDirectory))
        {
            startInfo.WorkingDirectory = cli.WorkingDirectory;
        }

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
    }

    public bool HasExited => _started && _process.HasExited;

    public void Start()
    {
        _process.Start();
        _started = true;
    }

    public async Task WriteLineAsync(string line, CancellationToken cancellationToken = default)
    {
        await _process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<string> ReadLinesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await _process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                yield break;
            }

            yield return line;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_started && !_process.HasExited)
        {
            try
            {
                _process.StandardInput.Close();
                if (!_process.WaitForExit(TimeSpan.FromSeconds(3)))
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Process already exited between the HasExited check and Close/Kill.
            }
        }

        _process.Dispose();
        await Task.CompletedTask;
    }
}

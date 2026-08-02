using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Assistant;

// The assistant's memory as a markdown file next to `cockpit.json` (AC-595). Plain text a human can open,
// because opening it is the only way to prune it.
// Not in `cockpit.json`: that file is written whole by `CockpitConfigFileAccess` on every settings
// change, and a growing block of free text in it would ride along with every one of those writes.
internal sealed class AssistantMemoryFile : IAssistantMemory, ISingletonService
{
    private const string Heading = "# What the operator asked me to remember";

    private const string StateHeading = "# Where the conversation stood when I last restarted";

    private readonly string _filePath;

    private readonly string _statePath;

    public AssistantMemoryFile()
        : this(CockpitConfigPath.AssistantMemory, CockpitConfigPath.AssistantCurrentState)
    {
    }

    // Test seam: point the memory at arbitrary files.
    internal AssistantMemoryFile(string filePath, string statePath)
    {
        _filePath = filePath;
        _statePath = statePath;
    }

    public Task<string> ReadAsync(CancellationToken cancellationToken = default) =>
        _ReadAsync(_filePath, cancellationToken);

    public Task<string> ReadCurrentStateAsync(CancellationToken cancellationToken = default) =>
        _ReadAsync(_statePath, cancellationToken);

    // Overwrites, where `RememberAsync` appends — and in a second file rather than a section of the
    // first, so neither write has to parse or hold a lock over the other's lines.
    public async Task NoteCurrentStateAsync(string text, CancellationToken cancellationToken = default)
    {
        var state = text?.Trim();
        ArgumentException.ThrowIfNullOrWhiteSpace(state);

        CockpitConfigPath.EnsurePrivateDirectory(Path.GetDirectoryName(_statePath) ?? CockpitConfigPath.Root);

        await File.WriteAllTextAsync(
            _statePath,
            $"{StateHeading}{Environment.NewLine}{Environment.NewLine}{state}{Environment.NewLine}",
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> _ReadAsync(string path, CancellationToken cancellationToken)
    {
        // A machine that has never remembered anything is the ordinary case on first run, not a failure to start.
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        return (await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)).Trim();
    }

    // ponytail: append-only, so the file only ever grows. It is one line per thing the operator said to keep, and
    // pruning is opening it — worth a second look on the day it is long enough to weigh on the launch instruction.
    public async Task RememberAsync(string text, CancellationToken cancellationToken = default)
    {
        var line = text?.Trim();
        ArgumentException.ThrowIfNullOrWhiteSpace(line);

        CockpitConfigPath.EnsurePrivateDirectory(Path.GetDirectoryName(_filePath) ?? CockpitConfigPath.Root);

        var isNew = !File.Exists(_filePath);
        var entry = $"- {DateTimeOffset.Now:yyyy-MM-dd} — {line.ReplaceLineEndings(" ")}{Environment.NewLine}";

        await File.AppendAllTextAsync(
            _filePath,
            isNew ? $"{Heading}{Environment.NewLine}{Environment.NewLine}{entry}" : entry,
            cancellationToken).ConfigureAwait(false);
    }
}

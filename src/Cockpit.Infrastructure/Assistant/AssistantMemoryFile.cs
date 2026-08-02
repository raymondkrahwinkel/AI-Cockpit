using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Assistant;

/// <summary>
/// The assistant's memory as a markdown file next to <c>cockpit.json</c> (AC-595). Plain text a human can open,
/// because opening it is the only way to prune it.
/// </summary>
/// <remarks>
/// Not in <c>cockpit.json</c>: that file is written whole by <c>CockpitConfigFileAccess</c> on every settings
/// change, and a growing block of free text in it would ride along with every one of those writes.
/// </remarks>
internal sealed class AssistantMemoryFile : IAssistantMemory, ISingletonService
{
    private const string Heading = "# What the operator asked me to remember";

    private readonly string _filePath;

    public AssistantMemoryFile()
        : this(CockpitConfigPath.AssistantMemory)
    {
    }

    /// <summary>Test seam: point the memory at an arbitrary file.</summary>
    internal AssistantMemoryFile(string filePath)
    {
        _filePath = filePath;
    }

    public async Task<string> ReadAsync(CancellationToken cancellationToken = default)
    {
        // A machine that has never remembered anything is the ordinary case on first run, not a failure to start.
        if (!File.Exists(_filePath))
        {
            return string.Empty;
        }

        return (await File.ReadAllTextAsync(_filePath, cancellationToken).ConfigureAwait(false)).Trim();
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

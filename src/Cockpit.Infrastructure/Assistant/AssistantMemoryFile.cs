using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Infrastructure.Swarm;
using Cockpit.Infrastructure.Mcp;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.Infrastructure.Assistant;

// The assistant's memory as a markdown file next to `cockpit.json` (AC-595). Plain text a human can open,
// because opening it is the only way to prune it. Not in `cockpit.json`: that file is written whole on every
// settings change, and a growing block of free text would ride along with every one of those writes.
internal sealed class AssistantMemoryFile : IAssistantMemory, ISingletonService
{
    private const string Heading = "# What the operator asked me to remember";

    private const string MachineHeading = "# What I know about this machine";

    private const string StateHeading = "# Where the conversation stood when I last restarted";

    private readonly string _filePath;

    private readonly string _statePath;

    private readonly string _machinePath;

    private readonly string _orgRulesPath;
    private readonly IServiceProvider? _services;

    public AssistantMemoryFile(IServiceProvider services)
        : this(CockpitConfigPath.AssistantMemory, CockpitConfigPath.AssistantCurrentState, CockpitConfigPath.AssistantMachineMemory, services)
    {
    }

    // Test seam: point the memory at arbitrary files.
    internal AssistantMemoryFile(string filePath, string statePath, string? machinePath = null, IServiceProvider? services = null)
    {
        _services = services;
        _filePath = filePath;
        _statePath = statePath;
        _machinePath = machinePath ?? Path.Combine(Path.GetDirectoryName(filePath)!, "assistant-machine.md");
        _orgRulesPath = CockpitConfigPath.AssistantOrgRulesFor(Path.GetDirectoryName(filePath) ?? CockpitConfigPath.Root);
    }

    public Task<string> ReadAsync(AssistantMemoryScope scope, CancellationToken cancellationToken = default) =>
        _ReadAsync(_PathFor(scope), cancellationToken);

    public Task<string> ReadCurrentStateAsync(CancellationToken cancellationToken = default) =>
        _ReadAsync(_statePath, cancellationToken);

    // Only read here: `_PathFor` never answers with it, so no remember, note or import can reach it (AC-1567).
    public Task<string> ReadOrgRulesAsync(CancellationToken cancellationToken = default) =>
        _ReadAsync(_orgRulesPath, cancellationToken);

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
        if (_services?.GetService<SwarmTakeoverCoordinator>() is { } takeover) await takeover.PublishAsync(cancellationToken);
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
    public async Task RememberAsync(
        string text,
        AssistantMemoryScope scope,
        CancellationToken cancellationToken = default)
    {
        var line = text?.Trim();
        ArgumentException.ThrowIfNullOrWhiteSpace(line);

        var path = _PathFor(scope);
        CockpitConfigPath.EnsurePrivateDirectory(Path.GetDirectoryName(path) ?? CockpitConfigPath.Root);

        var isNew = !File.Exists(path);
        var entry = $"- {DateTimeOffset.Now:yyyy-MM-dd} — {line.ReplaceLineEndings(" ")}{Environment.NewLine}";

        await File.AppendAllTextAsync(
            path,
            isNew ? $"{_HeadingFor(scope)}{Environment.NewLine}{Environment.NewLine}{entry}" : entry,
            cancellationToken).ConfigureAwait(false);
        if (_services?.GetService<SwarmTakeoverCoordinator>() is { } takeover) await takeover.PublishAsync(cancellationToken);
    }

    // AC-1929: when the note state last changed, which a merge after a split compares; a restore carries it over.
    internal DateTimeOffset NoteStateChangedAt() =>
        File.Exists(_statePath) ? new DateTimeOffset(File.GetLastWriteTimeUtc(_statePath)) : DateTimeOffset.MinValue;

    internal void RestoreStandby(string noteState, string behaviour, DateTimeOffset noteStateAt = default)
    {
        var existing = File.Exists(_filePath) ? File.ReadAllText(_filePath) : "";
        var carried = BehaviourMemorySync._Entries(behaviour).Select(BehaviourMemorySync._StripOrigin).ToHashSet(StringComparer.Ordinal);
        var localOnly = BehaviourMemorySync._Entries(existing).Where(line => !carried.Contains(BehaviourMemorySync._StripOrigin(line)));
        var merged = string.Join(Environment.NewLine, localOnly.Select(line => "- " + line));
        CockpitConfigPath.ReplaceAtomicallyPrivate(_statePath, noteState);
        if (noteStateAt != default)
        {
            File.SetLastWriteTimeUtc(_statePath, noteStateAt.UtcDateTime);
        }
        CockpitConfigPath.ReplaceAtomicallyPrivate(_filePath, string.IsNullOrEmpty(merged) ? behaviour : behaviour + Environment.NewLine + merged);
    }

    private string _PathFor(AssistantMemoryScope scope) => scope == AssistantMemoryScope.Machine ? _machinePath : _filePath;

    private static string _HeadingFor(AssistantMemoryScope scope) =>
        scope == AssistantMemoryScope.Machine ? MachineHeading : Heading;

    // Synchronous file I/O (AC-657): both files are a few kilobytes of markdown, and the loose backup/restore this
    // serves is already behind an MCP consent gate that awaits elsewhere — no need to thread cancellation through
    // ZipFile's own sync-only API for that.
    public Task<IReadOnlyList<string>> ExportAsync(string archivePath, CancellationToken cancellationToken = default) =>
        Task.FromResult(AssistantMemoryBackup.Write(archivePath, _filePath, _statePath, _machinePath));

    public Task<IReadOnlyList<string>> ImportAsync(string archivePath, CancellationToken cancellationToken = default) =>
        Task.FromResult(AssistantMemoryBackup.Restore(archivePath, _filePath, _statePath, _machinePath));
}

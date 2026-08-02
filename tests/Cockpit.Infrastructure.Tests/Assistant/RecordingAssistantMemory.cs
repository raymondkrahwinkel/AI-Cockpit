using Cockpit.Core.Abstractions.Assistant;

namespace Cockpit.Infrastructure.Tests.Assistant;

/// <summary>An in-memory <see cref="IAssistantMemory"/> that records what was written, for tests about the tool rather than the file.</summary>
internal sealed class RecordingAssistantMemory : IAssistantMemory
{
    public List<string> Remembered { get; } = [];

    public string Contents { get; set; } = string.Empty;

    public Task<string> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Contents);

    public Task RememberAsync(string text, CancellationToken cancellationToken = default)
    {
        Remembered.Add(text);
        return Task.CompletedTask;
    }
}

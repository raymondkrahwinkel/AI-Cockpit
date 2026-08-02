using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.YouTrack.Tests;

// An `ICockpitActions` whose "is there a session?" answer the test sets, and which records what was
// injected instead of pushing it anywhere — what "Add to prompt" needs to be exercised without a live session.
internal sealed class FakeCockpitActions : ICockpitActions
{
    public bool HasActiveSession { get; set; }

    public List<string> Injected { get; } = [];

    public List<string> Clipboard { get; } = [];

    public Task SetClipboardTextAsync(string text)
    {
        Clipboard.Add(text);
        return Task.CompletedTask;
    }

    public Task InjectIntoActiveSessionAsync(string text)
    {
        Injected.Add(text);
        return Task.CompletedTask;
    }
}

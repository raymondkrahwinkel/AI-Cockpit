using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitStatus.Tests;

// An `ICockpitActions` that records what it was asked to inject or copy, so a test can assert what a control's click handler sent it.
internal sealed class FakeCockpitActions : ICockpitActions
{
    public bool HasActiveSession { get; set; }

    public string? InjectedText { get; private set; }

    public string? ClipboardText { get; private set; }

    public Task SetClipboardTextAsync(string text)
    {
        ClipboardText = text;
        return Task.CompletedTask;
    }

    public Task InjectIntoActiveSessionAsync(string text)
    {
        InjectedText = text;
        return Task.CompletedTask;
    }
}

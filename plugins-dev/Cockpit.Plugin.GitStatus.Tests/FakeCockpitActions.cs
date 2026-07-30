using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitStatus.Tests;

/// <summary>An <see cref="ICockpitActions"/> that does nothing and records nothing — enough to construct the dialog with, when a test has no need of it.</summary>
internal sealed class FakeCockpitActions : ICockpitActions
{
    public bool HasActiveSession { get; set; }

    public Task SetClipboardTextAsync(string text) => Task.CompletedTask;

    public Task InjectIntoActiveSessionAsync(string text) => Task.CompletedTask;
}

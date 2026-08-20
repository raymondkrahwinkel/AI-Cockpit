using Avalonia.Controls;
using Cockpit.Plugin.Diagram.Collab;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.Diagram.Tests;

// AC-910 criterion 9: an Ask needs no read/edit consent — SendAsync reaches IPluginSessionBinding directly, with no
// capability gate in between. Locked in here so a later "for tidiness" consent check does not slip into the one
// shared path AskFlyout's callers all send through.
public class SurfaceSessionBindingTests
{
    [Fact]
    public async Task SendAsync_ReachesTheBoundSession_WithNoConsentGateInBetween()
    {
        var binding = new _FakeBinding("pane-1", live: true);
        var host = new _FakeHost(binding);
        var surfaceBinding = new SurfaceSessionBinding(host, "pane-1", () => { });

        await surfaceBinding.SendAsync("🗨️ Ask the agent · diagram \"Flow\" (id d1) — explain this");

        Assert.Equal("🗨️ Ask the agent · diagram \"Flow\" (id d1) — explain this", binding.SentText);
    }

    private sealed class _FakeHost(_FakeBinding binding) : ICockpitHost
    {
        public IServiceProvider Services => throw new NotSupportedException();

        public ICockpitActions Actions => throw new NotSupportedException();

        public IPluginStorage Storage => throw new NotSupportedException();

        public IPluginSessionBinding BindToSession(string paneId) => binding;

        public void ShowToast(string message, PluginToastSeverity severity = PluginToastSeverity.Information, string? actionLabel = null, Action? onAction = null)
        {
        }

        public void AddSettings(Func<Control> createView)
        {
        }

        public void AddSideMenuButton(string title, Action onInvoke)
        {
        }

        public void AddSideMenuSection(string title, Func<Control> createView)
        {
        }

        public Task ShowDialogAsync(string title, Func<Control> createContent, double width = 720, double height = 560) =>
            Task.CompletedTask;
    }

    private sealed class _FakeBinding(string paneId, bool live) : IPluginSessionBinding
    {
        public string? SentText { get; private set; }

        public string PaneId => paneId;

        public string? SessionName => "Werksessie";

        public bool IsLive => live;

        public event EventHandler? Ended { add { } remove { } }

        // Structurally the whole point of this test: nothing but the text crosses this seam — no consent request,
        // no capability check, no gate of any kind.
        public Task SendAsync(string text)
        {
            SentText = text;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }
}

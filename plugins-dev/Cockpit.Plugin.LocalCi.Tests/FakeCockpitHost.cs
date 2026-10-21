using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.LocalCi.Tests;

// Only what the MCP tools actually take from the host: who called them, what the operator said, and (AC-1535) the
// inbox the verdict goes to. Everything else on `ICockpitHost` has a default the tools never reach.
internal sealed class FakeCockpitHost : ICockpitHost
{
    public string? CallerPaneId { get; set; }

    public ConsentOutcome Answer { get; set; } = ConsentOutcome.Approved;

    public List<ConsentRequest> Asked { get; } = [];

    public string? CurrentMcpCallerPaneId => CallerPaneId;

    private readonly TaskCompletionSource<string> _delivered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // The first verdict a run delivered to a session's inbox.
    public Task<string> Delivered => _delivered.Task.WaitAsync(TimeSpan.FromSeconds(30));

    public Task<bool> DeliverToSessionInboxAsync(string paneId, string kind, string body)
    {
        _delivered.TrySetResult(body);
        return Task.FromResult(true);
    }

    public Task<ConsentDecision> RequestConsentAsync(ConsentRequest request)
    {
        Asked.Add(request);
        return Task.FromResult(new ConsentDecision(Answer));
    }

    public IServiceProvider Services => throw new NotSupportedException("No test reaches the service provider.");

    public ICockpitActions Actions => throw new NotSupportedException("No test reaches the cockpit actions.");

    public IPluginStorage Storage { get; } = new InMemoryStorage();

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

    private sealed class InMemoryStorage : IPluginStorage
    {
        private readonly Dictionary<string, object?> _values = [];

        public T? Get<T>(string key) => _values.GetValueOrDefault(key) is T value ? value : default;

        public void Set<T>(string key, T value) => _values[key] = value;
    }
}

// One session, as a header item would be handed it.
internal sealed class FakeSession(string paneId, string? workingDirectory) : IPluginSessionContext
{
    public string PaneId => paneId;

    public string? WorkingDirectory { get; set; } = workingDirectory;

    public event EventHandler? WorkingDirectoryChanged
    {
        add { }
        remove { }
    }

    public event EventHandler<SessionOutputText>? OutputProduced
    {
        add { }
        remove { }
    }
}

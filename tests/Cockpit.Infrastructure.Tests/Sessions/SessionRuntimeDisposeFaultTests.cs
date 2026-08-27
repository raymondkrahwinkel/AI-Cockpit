using System.Runtime.CompilerServices;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.Infrastructure.Tests.Sessions;

public class SessionRuntimeDisposeFaultTests
{
    [Fact]
    public async Task DisposeAsync_DisposesDriverAndMemoryCap_WhenEventPumpFaults()
    {
        var driver = new _Driver(streamFaults: true, disposeFaults: false);
        var limiter = new _Limiter();
        var runtime = new SessionRuntime(new _Factory(driver), profile: null, limiter);
        var events = new List<SessionEvent>();
        runtime.EventAppended += events.Add;

        await runtime.StartAsync(profile: null);
        await driver.StreamFinished;

        await runtime.DisposeAsync();

        Assert.True(driver.Disposed);
        Assert.True(limiter.Released);
        Assert.Contains(events, evt => evt is SessionError);
    }

    [Fact]
    public async Task DisposeAsync_ReleasesMemoryCap_WhenDriverDisposeFaults()
    {
        var driver = new _Driver(streamFaults: false, disposeFaults: true);
        var limiter = new _Limiter();
        var runtime = new SessionRuntime(new _Factory(driver), profile: null, limiter);

        await runtime.StartAsync(profile: null);
        await driver.StreamFinished;

        await runtime.DisposeAsync();

        Assert.True(limiter.Released);
    }

    private sealed class _Factory(ISessionDriver driver) : ISessionDriverFactory
    {
        public ISessionDriver Create(SessionProfile? profile) => driver;
    }

    private sealed class _Limiter : ISessionMemoryLimiter
    {
        public bool Released { get; private set; }

        public IDisposable? Apply(int processId, long capBytes) => new _Release(() => Released = true);

        private sealed class _Release(Action release) : IDisposable
        {
            public void Dispose() => release();
        }
    }

    private sealed class _Driver(bool streamFaults, bool disposeFaults) : ISessionDriver
    {
        private readonly TaskCompletionSource _streamFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StreamFinished => _streamFinished.Task;
        public bool Disposed { get; private set; }
        public SessionCapabilities Capabilities => new(false, false, false, false, false, false, false, false);
        public int? ProcessId => 4242;
        public string? SessionId => "conversation-1";
        public SessionProfile? Profile => null;
        public IAsyncEnumerable<SessionEvent> Events => _StreamAsync();

        private async IAsyncEnumerable<SessionEvent> _StreamAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new AssistantTextCompleted { SessionId = "conversation-1", Text = "hello" };
            await Task.Yield();
            _streamFinished.TrySetResult();
            if (streamFaults) throw new InvalidOperationException("provider stream died");
        }

        public Task StartAsync(SessionProfile? profile = null, string? permissionMode = null, string? model = null, IReadOnlySet<string>? enabledMcpServerNames = null, string? workingDirectory = null, SessionResume? resume = null, IReadOnlyDictionary<string, string>? launchOptions = null, string? projectId = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendUserMessageAsync(string text, IReadOnlyList<ImageAttachment>? images = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetPermissionModeAsync(string mode, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetModelAsync(string? model, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetMaxThinkingTokensAsync(int maxThinkingTokens, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task InterruptAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RespondToPermissionAsync(string toolUseId, bool allow, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AllowPermissionAlwaysAsync(string toolUseId, string toolName, string proposedInputJson, PermissionRuleScope scope, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return disposeFaults ? ValueTask.FromException(new InvalidOperationException("driver dispose died")) : ValueTask.CompletedTask;
        }
    }
}

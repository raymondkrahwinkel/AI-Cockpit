using System.Security.Cryptography;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Infrastructure.Consent;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Plugins.Abstractions.Channels;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Plugins;

// The capability lives only in this channel's memory and never in a model-visible tool or transcript.
public sealed partial class OperatorActionService : IOperatorActionService, ISingletonService
{
    private readonly ILogger<OperatorActionService> _logger;
    private readonly IConsentBroker? _consent;
    private readonly ISessionRegistry? _sessions;
    private readonly Lock _sourceGate = new();
    private OperatorActionSources? _sources;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, _Registration> _actions = new(StringComparer.Ordinal);
    private readonly HashSet<_Channel> _channels = [];

    public OperatorActionService(ILogger<OperatorActionService> logger, IConsentBroker? consent = null, ISessionRegistry? sessions = null,
        ISessionLauncher? launcher = null, ISessionProfileStore? profiles = null)
    {
        _logger = logger;
        _consent = consent;
        _sessions = sessions;
        _launcher = launcher;
        _profiles = profiles;
    }

    public IDisposable Register(OperatorAction action, Func<string, Task<bool>> respond, Func<bool> isCurrent)
    {
        var registration = new _Registration(this, action with { Decisions = action.Decisions.ToArray() }, respond, isCurrent);
        lock (_gate)
        {
            if (!_actions.TryAdd(action.Id, registration))
            {
                throw new InvalidOperationException("An operator action with this id already exists.");
            }
        }

        _Changed();
        return registration;
    }

    internal IDisposable RegisterQuestion(OperatorAction action, Func<IReadOnlyList<IReadOnlyList<string>>, Task<bool>> answer, Func<bool> isCurrent)
    {
        var registration = new _Registration(this, action, _ => Task.FromResult(false), isCurrent) { Answer = answer };
        lock (_gate)
        {
            _actions.Add(action.Id, registration);
        }
        _Changed();
        return registration;
    }

    internal IOperatorActionChannel OpenChannel(string? deviceId = null)
    {
        // The returned object is the authenticated device binding; no response can select another channel by id.
        var channel = new _Channel(this, deviceId ?? Guid.NewGuid().ToString("N"));
        lock (_sourceGate)
        {
            lock (_gate)
            {
                _channels.Add(channel);
            }

            if (_sources is null && _consent is not null)
            {
                _sources = new OperatorActionSources(this, _consent, _sessions, _logger);
            }
        }

        return channel;
    }

    private void _Close(_Channel channel)
    {
        lock (_sourceGate)
        {
            bool last;
            lock (_gate)
            {
                _channels.Remove(channel);
                channel.Capabilities.Clear();
                last = _channels.Count == 0;
            }

            if (last)
            {
                _sources?.Dispose();
                _sources = null;
            }
        }
    }

    private IReadOnlyList<OperatorPendingAction> _Snapshot(_Channel channel)
    {
        List<_Registration> actions;
        lock (_gate)
        {
            actions = [.. _actions.Values];
        }

        var current = actions.Where(item => item.IsCurrent()).ToList();
        lock (_gate)
        {
            if (!_channels.Contains(channel))
            {
                return [];
            }

            return [.. current.Where(item => _actions.GetValueOrDefault(item.Action.Id) == item).Select(item =>
            {
                var action = item.Action;
                var capability = string.Empty;
                if (!item.InFlight && !channel.Capabilities.TryGetValue(action.Id, out capability))
                {
                    capability = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                    channel.Capabilities.Add(action.Id, capability);
                }

                return new OperatorPendingAction(action.Id, action.Revision, action.Kind, action.Target, action.Summary, item.InFlight ? [] : action.Decisions, capability)
                {
                    Title = action.Title, Risk = action.Risk, SourcePlugin = action.SourcePlugin,
                    Questions = action.Questions,
                };
            })];
        }
    }

    private async Task<OperatorActionResult> _RespondAsync(_Channel channel, string id, string revision, string capability, string decision,
        IReadOnlyList<IReadOnlyList<string>>? answers = null)
    {
        _Registration? registration;
        lock (_gate)
        {
            if (McpRequestContext.CurrentCaller is not null || !_channels.Contains(channel)
                || !channel.Capabilities.TryGetValue(id, out var expected) || !string.Equals(expected, capability, StringComparison.Ordinal)
                || !_actions.TryGetValue(id, out registration) || registration.InFlight || registration.Action.Revision != revision
                || (answers is null ? !registration.Action.Decisions.Contains(decision, StringComparer.Ordinal)
                    : registration.Answer is null || answers.Count != registration.Action.Questions.Count
                        || registration.Action.Questions.Where((question, index) => !Assistant.OperatorMessageRegistry.Takes(question, answers[index])).Any()))
            {
                return new OperatorActionResult(false, "This action is no longer available for this device.");
            }
        }

        if (!registration.IsCurrent())
        {
            return new OperatorActionResult(false, "This action was already handled.");
        }

        lock (_gate)
        {
            if (!_channels.Contains(channel) || _actions.GetValueOrDefault(id) != registration || registration.InFlight)
            {
                return new OperatorActionResult(false, "This action was already handled.");
            }
            registration.InFlight = true;
            foreach (var other in _channels)
            {
                other.Capabilities.Remove(id);
            }
        }

        _Changed();
        var accepted = false;
        try
        {
            accepted = answers is not null && registration.Answer is { } answer
                ? await answer(answers).ConfigureAwait(false)
                : await registration.Respond(decision).ConfigureAwait(false);
            _logger.LogInformation("An operator decision from a phone was {Result}.", accepted ? "accepted" : "obsolete");
            return new OperatorActionResult(accepted, accepted ? null : "This action was already handled.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogWarning("A phone operator decision failed: {Reason}.", exception.GetType().Name);
            return new OperatorActionResult(false, "The action could not be completed. Reload the pending list.");
        }
        finally
        {
            var retryable = !accepted && registration.IsCurrent();
            lock (_gate)
            {
                if (_actions.GetValueOrDefault(id) == registration)
                {
                    if (retryable)
                    {
                        registration.InFlight = false;
                    }
                    else
                    {
                        _actions.Remove(id);
                    }
                }
            }

            _Changed();
        }
    }

    private void _Remove(_Registration registration)
    {
        lock (_gate)
        {
            if (!_actions.TryGetValue(registration.Action.Id, out var current) || !ReferenceEquals(current, registration))
            {
                return;
            }

            _actions.Remove(registration.Action.Id);
            foreach (var channel in _channels)
            {
                channel.Capabilities.Remove(registration.Action.Id);
            }
        }

        _Changed();
    }

    private void _Changed()
    {
        List<_Channel> channels;
        lock (_gate)
        {
            channels = [.. _channels];
        }

        foreach (var channel in channels)
        {
            try
            {
                channel.Publish();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _logger.LogWarning("Publishing pending operator actions failed: {Reason}.", exception.GetType().Name);
            }
        }
    }

    private sealed class _Registration(OperatorActionService owner, OperatorAction action, Func<string, Task<bool>> respond, Func<bool> isCurrent) : IDisposable
    {
        public OperatorAction Action { get; } = action;
        public Func<string, Task<bool>> Respond { get; } = respond;
        public Func<bool> IsCurrent { get; } = isCurrent;
        public Func<IReadOnlyList<IReadOnlyList<string>>, Task<bool>>? Answer { get; init; }
        public bool InFlight { get; set; }
        public void Dispose() => owner._Remove(this);
    }

    private sealed class _Channel(OperatorActionService owner, string deviceId) : IOperatorActionChannel
    {
        public string DeviceId { get; } = deviceId;
        public string SessionCapability { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        public bool CanManageSessions => owner._launcher is not null && owner._profiles is not null;
        public Task<OperatorSessionCatalog> SessionCatalogAsync() => owner._SessionCatalogAsync(this);
        public Task<OperatorSessionResult> StartSessionAsync(string requestId, string capability, string epoch, string deskId, string profileId) =>
            owner._StartSessionAsync(this, requestId, capability, epoch, deskId, profileId);
        public Task<OperatorSessionResult> StopSessionAsync(string capability, string paneId) => owner._StopSessionAsync(this, capability, paneId);
        public Dictionary<string, string> Capabilities { get; } = new(StringComparer.Ordinal);
        public IReadOnlyList<OperatorPendingAction> Snapshot => owner._Snapshot(this);
        public event EventHandler? Changed;
        public Task<OperatorActionResult> RespondAsync(string id, string revision, string capability, string decision) =>
            owner._RespondAsync(this, id, revision, capability, decision);
        public Task<OperatorActionResult> AnswerQuestionsAsync(string id, string revision, string capability, IReadOnlyList<IReadOnlyList<string>> answers) =>
            owner._RespondAsync(this, id, revision, capability, string.Empty, answers);
        public void Publish() => Changed?.Invoke(this, EventArgs.Empty);
        public void Dispose() => owner._Close(this);
    }
}

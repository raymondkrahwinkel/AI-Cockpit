using System.Security.Cryptography;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Infrastructure.Consent;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Plugins.Abstractions.Channels;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Plugins;

// The capability lives only in this channel's memory and never in a model-visible tool or transcript.
public sealed class OperatorActionService : IOperatorActionService, ISingletonService
{
    private readonly ILogger<OperatorActionService> _logger;
    private readonly IConsentBroker? _consent;
    private readonly ISessionRegistry? _sessions;
    private readonly Lock _sourceGate = new();
    private OperatorActionSources? _sources;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, _Registration> _actions = new(StringComparer.Ordinal);
    private readonly HashSet<_Channel> _channels = [];

    public OperatorActionService(ILogger<OperatorActionService> logger, IConsentBroker? consent = null, ISessionRegistry? sessions = null)
    {
        _logger = logger;
        _consent = consent;
        _sessions = sessions;
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

    public IOperatorActionChannel OpenChannel(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        // The returned object is the authenticated device binding; no response can select another channel by id.
        var channel = new _Channel(this);
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
                if (!channel.Capabilities.TryGetValue(action.Id, out var capability))
                {
                    capability = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                    channel.Capabilities.Add(action.Id, capability);
                }

                return new OperatorPendingAction(action.Id, action.Revision, action.Kind, action.Target, action.Summary, action.Decisions, capability);
            })];
        }
    }

    private async Task<OperatorActionResult> _RespondAsync(_Channel channel, string id, string revision, string capability, string decision)
    {
        _Registration? registration;
        lock (_gate)
        {
            if (McpRequestContext.CurrentCaller is not null || !_channels.Contains(channel)
                || !channel.Capabilities.TryGetValue(id, out var expected) || !string.Equals(expected, capability, StringComparison.Ordinal)
                || !_actions.TryGetValue(id, out registration) || registration.Action.Revision != revision
                || !registration.Action.Decisions.Contains(decision, StringComparer.Ordinal))
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
            if (!_channels.Contains(channel) || _actions.GetValueOrDefault(id) != registration)
            {
                return new OperatorActionResult(false, "This action was already handled.");
            }
            _actions.Remove(id);
            foreach (var other in _channels)
            {
                other.Capabilities.Remove(id);
            }
        }

        _Changed();
        try
        {
            var accepted = await registration.Respond(decision).ConfigureAwait(false);
            _logger.LogInformation("An operator decision from a phone was {Result}.", accepted ? "accepted" : "obsolete");
            return new OperatorActionResult(accepted, accepted ? null : "This action was already handled.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogWarning("A phone operator decision failed: {Reason}.", exception.GetType().Name);
            return new OperatorActionResult(false, "The action could not be completed. Reload the pending list.");
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
        public void Dispose() => owner._Remove(this);
    }

    private sealed class _Channel(OperatorActionService owner) : IOperatorActionChannel
    {
        public Dictionary<string, string> Capabilities { get; } = new(StringComparer.Ordinal);
        public IReadOnlyList<OperatorPendingAction> Snapshot => owner._Snapshot(this);
        public event EventHandler? Changed;
        public Task<OperatorActionResult> RespondAsync(string id, string revision, string capability, string decision) =>
            owner._RespondAsync(this, id, revision, capability, decision);
        public void Publish() => Changed?.Invoke(this, EventArgs.Empty);
        public void Dispose() => owner._Close(this);
    }
}

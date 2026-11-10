using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Consent;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Consent;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Plugins;

// Native request owners retain the final atomic check, shared with their desktop buttons.
internal sealed class OperatorActionSources : IDisposable
{
    private readonly OperatorActionService _actions;
    private readonly IConsentBroker _consent;
    private readonly ISessionRegistry? _sessions;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, IDisposable> _prompts = [];
    private readonly Dictionary<ISessionHandle, Action<TranscriptRowUpsert>> _followed = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(ISessionHandle Handle, string ToolId), (SessionPendingPermission Permission, IDisposable Registration)> _permissions = [];
    private bool _disposed;

    public OperatorActionSources(OperatorActionService actions, IConsentBroker consent, ISessionRegistry? sessions, ILogger logger)
    {
        _actions = actions;
        _consent = consent;
        _sessions = sessions;
        _logger = logger;
        consent.PromptOpened += _Opened;
        consent.PromptClosed += _Closed;
        foreach (var prompt in consent.PendingPrompts)
        {
            _Opened(null, prompt);
        }

        if (sessions is not null)
        {
            sessions.Changed += _SessionsChanged;
            _SessionsChanged(null, EventArgs.Empty);
        }
    }

    private void _Opened(object? sender, ConsentPrompt prompt)
    {
        lock (_gate)
        {
            if (_disposed || _prompts.ContainsKey(prompt.Id))
            {
                return;
            }

            var needsChoices = prompt.Request.Choices is { Count: > 0 };
            _prompts[prompt.Id] = _actions.Register(
                new OperatorAction(prompt.Id.ToString("N"), Guid.NewGuid().ToString("N"), "consent", prompt.Request.Source.PaneId ?? "",
                    prompt.Request.Action, needsChoices ? ["deny"] : ["approve", "deny"])
                {
                    Title = prompt.Request.Title, Risk = prompt.Request.Risk.ToString(), SourcePlugin = prompt.Request.Source.PluginId,
                },
                decision => Task.FromResult(_consent.TryRespond(prompt.Id, decision == "approve" ? ConsentOutcome.Approved : ConsentOutcome.Denied, OperatorDecisionOrigin.PhoneOperator)),
                () => _consent.PendingPrompts.Any(current => current.Id == prompt.Id));
        }
    }

    private void _Closed(object? sender, Guid id)
    {
        lock (_gate)
        {
            if (_prompts.Remove(id, out var registration))
            {
                registration.Dispose();
            }
        }
    }

    private async void _SessionsChanged(object? sender, EventArgs args)
    {
        try
        {
            var live = _sessions is { } sessions ? sessions.All.Append(sessions.Assistant).OfType<ISessionHandle>().ToList() : [];
            List<ISessionHandle> added = [];
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                foreach (var gone in _followed.Keys.Where(handle => !live.Contains(handle)).ToList())
                {
                    gone.RowUpserted -= _followed[gone];
                    _followed.Remove(gone);
                    foreach (var key in _permissions.Keys.Where(key => ReferenceEquals(key.Handle, gone)).ToList())
                    {
                        _permissions[key].Registration.Dispose();
                        _permissions.Remove(key);
                    }
                }

                foreach (var handle in live.Where(handle => !_followed.ContainsKey(handle)))
                {
                    Action<TranscriptRowUpsert> changed = row => _Row(handle, row.Row);
                    _followed[handle] = changed;
                    handle.RowUpserted += changed;
                    added.Add(handle);
                }
            }

            foreach (var handle in added)
            {
                foreach (var permission in await handle.ReadPendingPermissionsAsync().ConfigureAwait(false))
                {
                    _OfferPermission(handle, permission);
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogWarning("Reading pending operator actions failed: {Reason}.", exception.GetType().Name);
        }
    }

    private void _Row(ISessionHandle handle, TranscriptSnapshotEntry row)
    {
        if (row.ToolUseId is not { } toolId || row.ParentRowId is not null)
        {
            return;
        }

        if (row.IsPendingPermission)
        {
            _OfferPermission(handle, new SessionPendingPermission(toolId, row.ToolName ?? "", row.InputJson ?? "{}", row.Timestamp));
        }
        else
        {
            lock (_gate)
            {
                if (_permissions.Remove((handle, toolId), out var registration))
                {
                    registration.Registration.Dispose();
                }
            }
        }
    }

    private void _OfferPermission(ISessionHandle handle, SessionPendingPermission permission)
    {
        var key = (handle, permission.ToolUseId);
        lock (_gate)
        {
            if (_disposed || !_followed.ContainsKey(handle))
            {
                return;
            }

            if (_permissions.TryGetValue(key, out var previous))
            {
                if (previous.Permission == permission)
                {
                    return;
                }

                previous.Registration.Dispose();
                _permissions.Remove(key);
            }

            var available = true;
            async Task<bool> Respond(string decision)
            {
                try
                {
                    return _IsLive(handle) && await handle.RespondToPermissionRevisionAsync(permission, decision == "approve", OperatorDecisionOrigin.PhoneOperator).ConfigureAwait(false);
                }
                finally
                {
                    Volatile.Write(ref available, false);
                }
            }

            _permissions[key] = (permission, _actions.Register(
                new OperatorAction(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "permission", handle.PaneId,
                    permission.ToolName + "\n" + permission.InputJson, ["approve", "deny"]),
                Respond,
                () => Volatile.Read(ref available) && _IsLive(handle)));
        }
    }

    private bool _IsLive(ISessionHandle handle) =>
        ReferenceEquals(_sessions?.Find(handle.PaneId), handle) || ReferenceEquals(_sessions?.Assistant, handle);

    public void Dispose()
    {
        _consent.PromptOpened -= _Opened;
        _consent.PromptClosed -= _Closed;
        if (_sessions is not null)
        {
            _sessions.Changed -= _SessionsChanged;
        }

        lock (_gate)
        {
            _disposed = true;
            foreach (var pair in _followed)
            {
                pair.Key.RowUpserted -= pair.Value;
            }

            _followed.Clear();
            foreach (var registration in _prompts.Values.Concat(_permissions.Values.Select(offer => offer.Registration)))
            {
                registration.Dispose();
            }

            _prompts.Clear();
            _permissions.Clear();
        }
    }
}

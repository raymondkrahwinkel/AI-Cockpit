using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Sessions;

public sealed partial class SessionHost
{
    private Action<Action> _onOwnerThread = action => action();
    private ClaimedReadOnlyTurn? _readOnlyTurn;

    public ReadOnlyTurnReceipt TryStartReadOnlyTurn(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Runtime is not { IsRunning: true, SupportsReadOnlyTurns: true } runtime)
        {
            return ReadOnlyTurnReceipt.Refused(ReadOnlyTurnAdmission.Unavailable);
        }

        if (IsBusy || _readOnlyTurn is not null || TurnsHeldBecause is not null || Queue.Count > 0)
        {
            return ReadOnlyTurnReceipt.Refused(ReadOnlyTurnAdmission.Busy);
        }

        var turn = new ClaimedReadOnlyTurn(new QueuedPrompt(text, []), runtime, cancellationToken);
        _readOnlyTurn = turn;
        IsBusy = true;
        Queue.Add(turn.Prompt);
        turn.Cancellation = cancellationToken.Register(() => _onOwnerThread(() => _CancelReadOnlyTurn(turn)));
        _Track(_StartReadOnlyTurnAsync(turn));
        return new ReadOnlyTurnReceipt(ReadOnlyTurnAdmission.Accepted, turn.Started.Task, turn.Completion.Task);
    }

    private void _CancelReadOnlyTurn(ClaimedReadOnlyTurn turn)
    {
        if (turn.HasStarted)
        {
            return;
        }

        Queue.Remove(turn.Prompt);
        turn.Started.TrySetCanceled();
        turn.Completion.TrySetCanceled();
    }

    private async Task _StartReadOnlyTurnAsync(ClaimedReadOnlyTurn turn)
    {
        try
        {
            // Keep the reservation until enforcement is armed, even if the caller withdraws meanwhile.
            await turn.Runtime.SetReadOnlyTurnAsync(true).ConfigureAwait(false);
            Task sending = await _OnOwnerThreadAsync(() =>
            {
                if (turn.Token.IsCancellationRequested || turn.Started.Task.IsCanceled || !Queue.Contains(turn.Prompt))
                {
                    _CancelReadOnlyTurn(turn);
                    return Task.CompletedTask;
                }

                if (!ReferenceEquals(Runtime, turn.Runtime) || !turn.Runtime.IsRunning || TurnsHeldBecause is not null)
                {
                    throw new InvalidOperationException("The assistant became unavailable before its turn started.");
                }

                Queue.Remove(turn.Prompt);
                turn.HasStarted = true;
                turn.Started.TrySetResult();
                return DispatchAsync(turn.Prompt);
            }).ConfigureAwait(false);
            await sending.ConfigureAwait(false);
            if (turn.HasStarted)
            {
                return;
            }
        }
        catch (Exception exception)
        {
            turn.Started.TrySetException(exception);
            turn.Completion.TrySetException(exception);
        }

        await _ReleaseReadOnlyTurnAsync(turn, dispatchNext: true).ConfigureAwait(false);
    }

    private async Task _ReleaseReadOnlyTurnAsync(ClaimedReadOnlyTurn turn, bool dispatchNext)
    {
        if (Interlocked.Exchange(ref turn.IsReleasing, 1) != 0)
        {
            return;
        }

        try
        {
            if (!turn.IsStopping)
            {
                await turn.Runtime.SetReadOnlyTurnAsync(false).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            // A failed restore stays closed: no later prompt may accidentally inherit a wider tool policy.
            _logger?.LogError(exception, "Could not release the assistant's read-only turn.");
            dispatchNext = false;
        }

        await _OnOwnerThreadAsync(() =>
        {
            turn.Cancellation.Unregister();
            Queue.Remove(turn.Prompt);
            if (ReferenceEquals(_readOnlyTurn, turn))
            {
                _readOnlyTurn = null;
                IsBusy = false;
                if (dispatchNext && ReferenceEquals(Runtime, turn.Runtime) && turn.Runtime.IsRunning)
                {
                    _DispatchNextQueued();
                }
            }

            return true;
        }).ConfigureAwait(false);
    }

    private Task<T> _OnOwnerThreadAsync<T>(Func<T> action)
    {
        var completed = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _onOwnerThread(() =>
        {
            try
            {
                completed.SetResult(action());
            }
            catch (Exception exception)
            {
                completed.SetException(exception);
            }
        });
        return completed.Task;
    }

    private sealed class ClaimedReadOnlyTurn(QueuedPrompt prompt, ISessionRuntime runtime, CancellationToken token)
    {
        public QueuedPrompt Prompt { get; } = prompt;
        public ISessionRuntime Runtime { get; } = runtime;
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Text { get; } = [];
        public CancellationTokenRegistration Cancellation { get; set; }
        public bool HasStarted { get; set; }
        public bool IsStopping { get; set; }
        public int IsReleasing;
    }
}

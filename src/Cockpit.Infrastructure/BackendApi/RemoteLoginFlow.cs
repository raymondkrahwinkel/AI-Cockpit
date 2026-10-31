using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.BackendApi;

public sealed class RemoteLoginFlow : ILoginFlow
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    private readonly BackendApiClient _client;
    private readonly string _path;
    private readonly object? _startBody;
    private readonly CancellationTokenSource _stop;
    private readonly CancellationToken _stopToken;
    private readonly Channel<LoginFlowStep> _steps = Channel.CreateUnbounded<LoginFlowStep>();
    private readonly TaskCompletionSource<string> _flowId = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task<LoginFlowResult> _run;

    public RemoteLoginFlow(BackendApiClient client, string profile, CancellationToken cancellationToken)
        : this(client, $"api/v1/profiles/{Uri.EscapeDataString(profile)}/sign-in", null, cancellationToken)
    {
    }

    internal RemoteLoginFlow(BackendApiClient client, string path, object? startBody, CancellationToken cancellationToken)
    {
        _client = client;
        _path = path;
        _startBody = startBody;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _stopToken = _stop.Token;
        _run = _RunAsync(_stopToken);
    }

    public IAsyncEnumerable<LoginFlowStep> Steps => _ReadStepsAsync(_stopToken);

    public Task<LoginFlowResult> Completion => _run;

    public async Task SubmitAsync(string value, CancellationToken cancellationToken)
    {
        var flowId = await _flowId.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_stopToken, cancellationToken);
        await _client.SendAsync<RemoteSignInState>(
            HttpMethod.Post,
            $"{_path}/{Uri.EscapeDataString(flowId)}/input",
            new { text = value },
            request.Token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _run.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // Completion carries the flow failure; closing its view must remain safe.
        }
        finally
        {
            _stop.Dispose();
        }
    }

    private async Task<LoginFlowResult> _RunAsync(CancellationToken cancellationToken)
    {
        LoginFlowStep? previous = null;
        try
        {
            var state = await _client.SendAsync<RemoteSignInState>(HttpMethod.Post, _path, _startBody, cancellationToken).ConfigureAwait(false);
            _flowId.TrySetResult(state.FlowId);
            while (true)
            {
                var step = _Step(state);
                if (step != previous)
                {
                    await _steps.Writer.WriteAsync(step, cancellationToken).ConfigureAwait(false);
                    previous = step;
                }

                if (!string.Equals(state.Status, "running", StringComparison.Ordinal))
                {
                    return new LoginFlowResult(
                        string.Equals(state.Status, "succeeded", StringComparison.Ordinal),
                        state.Error);
                }

                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                state = await _client.GetAsync<RemoteSignInState>(
                    $"{_path}/{Uri.EscapeDataString(state.FlowId)}",
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            _flowId.TrySetException(exception);
            throw;
        }
        finally
        {
            _steps.Writer.TryComplete();
        }
    }

    private async IAsyncEnumerable<LoginFlowStep> _ReadStepsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var step in _steps.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return step;
        }
    }

    private static LoginFlowStep _Step(RemoteSignInState state)
    {
        var message = string.IsNullOrWhiteSpace(state.Code)
            ? state.Message
            : $"{state.Message}{Environment.NewLine}Code: {state.Code}";
        return new LoginFlowStep(
            message,
            Uri.TryCreate(state.Url, UriKind.Absolute, out var link) ? link : null,
            state.AwaitsInput)
        {
            ExpiresAt = state.ExpiresAt,
        };
    }
}

internal sealed record RemoteSignInState(
    string FlowId,
    string Profile,
    string Status,
    string Message,
    string? Url,
    string? Code,
    bool AwaitsInput,
    DateTimeOffset ExpiresAt,
    string? Error);

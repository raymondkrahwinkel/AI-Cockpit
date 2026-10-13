namespace Cockpit.Infrastructure.Hosting;

// AC-1525: an admin's restart, handed to Cockpit.Server's stop path as if it were a SIGTERM. The process exits cleanly and
// its supervisor (compose `restart: unless-stopped`) starts it again; there is no restart without one. Registered only
// on a backend without a frontend, so a desktop answers the route with not-found.
public sealed class ServerRestartSignal
{
    private readonly TaskCompletionSource _requested = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Requested => _requested.Task;

    public void Request() => _requested.TrySetResult();
}

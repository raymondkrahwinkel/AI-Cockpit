namespace Cockpit.App.Services;

// AC-1525: "Restart now" in a plugin manager that administers a server restarts that server, not this cockpit.
internal sealed class ServerRestartService(Func<Task> restart) : IAppRestartService
{
    // The restart reports its own outcome; nothing is left for this caller to await.
    public void Restart() => _ = restart();
}

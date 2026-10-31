using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.Core.Abstractions.Secrets;
using Cockpit.Core.Configuration;
using Cockpit.Infrastructure.Hosting;

// AC-1987: a native gh executable for the remote-admin journey, never the operator's GitHub credentials.
if (Environment.GetEnvironmentVariable("COCKPIT_FAKE_GITHUB_STATE") is { } githubState
    && Path.IsPathRooted(githubState)
    && Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "gh")
{
    if (args.Length < 2 || args[0] != "auth")
    {
        return 2;
    }

    switch (args[1])
    {
        case "status":
            if (!File.Exists(githubState))
            {
                return 1;
            }
            Console.Error.WriteLine("github.com\n  \u2713 Logged in to github.com account fake-gh (keyring)");
            return 0;
        case "login":
            if (args.Contains("--with-token"))
            {
                if (string.IsNullOrWhiteSpace(await Console.In.ReadLineAsync()))
                {
                    return 1;
                }
                await File.WriteAllTextAsync(githubState, "fake-gh");
                return 0;
            }
            Console.Error.WriteLine("! First copy your one-time code: ABCD-EFGH");
            Console.Error.WriteLine("Press Enter to open https://github.com/login/device in your browser...");
            while (!File.Exists(githubState))
            {
                await Task.Delay(25);
            }
            return 0;
        case "setup-git":
            return 0;
        case "logout":
            File.Delete(githubState);
            return 0;
        default:
            return 2;
    }
}

// Usage: Cockpit.ServerSeed <absolute state root> <unlock password file>
if (args.Length != 2 || !Path.IsPathRooted(args[0]))
{
    Console.Error.WriteLine("Usage: Cockpit.ServerSeed <absolute state root> <unlock password file>");
    return 2;
}

var password = (await File.ReadAllTextAsync(args[1])).TrimEnd('\n');
Environment.SetEnvironmentVariable(CockpitBuild.StateRootVariable, args[0]);
var backend = CockpitBackend.Build(NullLoggerFactory.Instance);
await using var services = backend.Services;
await services.GetRequiredService<ISecretProtectionService>().EnableAsync(password);
return 0;

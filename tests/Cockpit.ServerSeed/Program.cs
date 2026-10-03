using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Secrets;
using Cockpit.Core.Configuration;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Hosting;

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
await services.GetRequiredService<INodeEndpointSettingsStore>().SaveAsync(new NodeEndpointSettings { Enabled = true });
await services.GetRequiredService<ISecretProtectionService>().EnableAsync(password);
return 0;

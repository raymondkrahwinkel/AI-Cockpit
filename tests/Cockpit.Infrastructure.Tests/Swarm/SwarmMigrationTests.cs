using Cockpit.Core.Mcp;
using Cockpit.Core.Swarm;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Swarm;

namespace Cockpit.Infrastructure.Tests.Swarm;

public class SwarmMigrationTests
{
    public static TheoryData<string> Cases => new() { "secret", "key", "stranger", "other-key", "server", "server-join" };

    internal static async Task OnlyTheExistingDesktopControllerCanMigrate(string scenario)
    {
        await using var pair = await SwarmJoinFixture.CreateAsync(scenario.StartsWith("server", StringComparison.Ordinal) ? SwarmHostKind.Server : SwarmHostKind.Desktop);
        var settings = await pair.B.Settings.LoadAsync();
        await pair.B.Settings.SaveAsync(settings with { SharedSecret = "existing-secret", Pairing = new NodePairing
        {
            ControllerName = "controller", ControllerAddress = "127.0.0.1", PairedAtUtc = DateTimeOffset.UtcNow,
        } });
        var credential = scenario == "stranger" ? "wrong" : "existing-secret";
        if (scenario is "key" or "other-key")
        {
            var issued = await pair.B.Keys.IssueAsync("controller", ConnectKeyCapability.Admin, null,
                new NodeCaller(null, "local", ConnectKeyCapability.Admin, "local", CancellationToken.None));
            credential = issued.Secret;
            if (scenario == "key") pair.B.Presence.Seen("controller", await pair.B.Keys.AuthenticateAsync(credential, null, "127.0.0.1"));
        }
        if (scenario is "secret" or "key")
        {
            await new SwarmMigration(pair.A.Client, pair.A.Join).MigrateAsync(pair.B.Address, credential, pair.B.Certificate.Fingerprint);
            Assert.Equal(2, (await pair.A.Membership.LoadAsync()).Members.Count());
            Assert.Equal(2, (await pair.B.Membership.LoadAsync()).Members.Count());
            await pair.AssertBothDirectionsAsync();
            Assert.Single(pair.A.Toast.Notifications);
            Assert.Single(pair.B.Toast.Notifications);
            Assert.NotNull((await pair.B.Settings.LoadAsync()).Pairing);
        }
        else
        {
            var failure = await Assert.ThrowsAsync<NodePairingException>(() => scenario == "server-join"
                ? pair.A.Client.BeginAsync(pair.B.Address)
                : pair.A.Client._BeginAsync(pair.B.Address, credential, pair.B.Certificate.Fingerprint, default));
            Assert.Equal(scenario.StartsWith("server", StringComparison.Ordinal) ? SwarmJoinError.IsAServer : NodePairingError.InvalidToken, failure.Error);
            Assert.Empty((await pair.B.Membership.LoadAsync()).Log);
        }
    }
}

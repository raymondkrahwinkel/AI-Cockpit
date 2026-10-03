using Cockpit.Plugins.Abstractions.Health;

namespace Cockpit.Plugin.Discord.Tests;

internal sealed class DiscordHealthStateTests
{
    internal static void Verify()
    {
        var tests = new DiscordHealthStateTests();
        tests.Read_ReportsConnectionBotDeliveryAndLastMessage();
        tests.Read_UsesAFixedFailureLabelWithoutAnErrorMessage();
    }

    private void Read_ReportsConnectionBotDeliveryAndLastMessage()
    {
        var at = new DateTimeOffset(2026, 10, 3, 21, 14, 0, TimeSpan.Zero);
        var health = new DiscordHealthState(new FixedClock(at));

        health.Connected("Zyra");
        health.DirectMessageDelivered();

        var online = Assert.Single(health.Read().Rows);
        Assert.Equal("Online as Zyra · DM delivery ok", online.Label);
        Assert.Equal(PluginHealthStatus.Ok, online.Status);
        Assert.Equal(at, online.At);

        health.Disconnected();

        var offline = Assert.Single(health.Read().Rows);
        Assert.Equal("Offline · DM delivery ok", offline.Label);
        Assert.Equal(PluginHealthStatus.Failed, offline.Status);
        Assert.Equal(at, offline.At);
    }

    private void Read_UsesAFixedFailureLabelWithoutAnErrorMessage()
    {
        var health = new DiscordHealthState(new FixedClock(DateTimeOffset.UnixEpoch));

        health.DirectMessageFailed();

        var row = Assert.Single(health.Read().Rows);
        Assert.Equal("Offline · DM delivery failed", row.Label);
        Assert.DoesNotContain("token", row.Label, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

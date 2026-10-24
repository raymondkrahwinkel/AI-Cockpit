using System.Globalization;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Backup;
using Cockpit.Core.Backup;
using Cockpit.Plugins.Abstractions.Backup;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-1662: Options → Backup says when and where the last scheduled run went, not only why one failed. The name of a
/// plugin target is known after <c>RefreshTargets</c>, and a run since the start shows when Options opens again.
/// </summary>
public sealed class Ac1662BackupStatusTests
{
    [Fact]
    public async Task Status_SaysWhenAndWhereTheLastRunWent_AndAStandingFailureWinsOverIt()
    {
        var done = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        var store = Substitute.For<IBackupScheduleStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new BackupSchedule { TargetId = "depot" });
        store.LoadStateAsync(Arg.Any<CancellationToken>()).Returns(new BackupScheduleState(LastSuccess: done));
        var depot = Substitute.For<IBackupTarget>();
        depot.Id.Returns("depot");
        depot.DisplayName.Returns("Depot: Staging");
        var viewModel = new BackupScheduleViewModel(store);

        await viewModel.LoadAsync();
        viewModel.RefreshTargets([depot]);

        Assert.Equal(
            $"Last backup: {done.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} to Depot: Staging.",
            viewModel.Status);

        store.LoadStateAsync(Arg.Any<CancellationToken>()).Returns(new BackupScheduleState(LastSuccess: done, LastFailure: "The disk is full."));
        await viewModel.RefreshStatusAsync();

        Assert.Equal("The disk is full.", viewModel.Status);
    }
}

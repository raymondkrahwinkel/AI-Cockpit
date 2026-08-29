using System.Runtime.CompilerServices;

// AC-1229: the one file in this assembly allowed to name the dispatcher — claiming it is what makes the ban stick.
#pragma warning disable RS0030
using Avalonia.Threading;

namespace Cockpit.Core.Tests;

// AC-1229: BannedSymbols.Avalonia.txt stops this assembly naming the dispatcher, but not a production method
// reaching one two layers down — which is what AC-1201 turned WorkspaceIdsByPane() into, and the analyzer saw
// nothing. Avalonia binds Dispatcher.UIThread to whoever touches it first, so this claims it on a parked thread
// before any test runs. A transitive hop then fails the same way alone as in the full suite, with the path in the
// stack trace, instead of flipping a coin between a false green and a five-second red that reads as a product
// timeout — the coin flip is what cost AC-1227 a seven-step bisect.
internal static class UiDispatcherSentinel
{
    [ModuleInitializer]
    internal static void Claim()
    {
        var claimed = new ManualResetEventSlim();
        new Thread(() =>
        {
            _ = Dispatcher.UIThread;
            claimed.Set();
            Thread.Sleep(Timeout.Infinite);
        })
        {
            IsBackground = true,
            Name = "core-tests-unpumped-dispatcher",
        }.Start();

        claimed.Wait(TimeSpan.FromSeconds(30));
    }
}

public class UiDispatcherSentinelTests
{
    // Red if the initializer stops running, or if Avalonia stops binding the dispatcher to its first toucher:
    // either way the inline CheckAccess() branch is reachable from a test thread again and the false green is back.
    [Fact]
    public void ATestThread_DoesNotOwnTheDispatcher_SoNoUiThreadCallCanRunInline() =>
        Assert.False(Dispatcher.UIThread.CheckAccess());
}

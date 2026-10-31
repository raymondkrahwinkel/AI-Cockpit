using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using Cockpit.App.Services;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.ViewTests;

[Collection("avalonia")]
public sealed class Ac1890FreezeStackTests
{
    [SkippableFact]
    public async Task Freeze_LogsKnownUiMethodWithoutArgumentValues()
    {
        var logger = new CapturingLogger();
        using var service = new DiagnosticsBackgroundService(logger);
        service.Start();
        await Task.Delay(1200);
        Assert.Contains(logger.Lines, line => line.Contains("snapshots="));
        Skip.IfNot(OperatingSystem.IsWindows(), "In-memory UI stack capture is Windows-only.");
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        await Dispatcher.UIThread.InvokeAsync(() => KnownSixSecondUiBlock("secret-prompt-value"));
        var stack = await logger.Stack.Task.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Contains(nameof(KnownSixSecondUiBlock), stack);
        Assert.DoesNotContain("secret-prompt-value", stack);
        Assert.InRange(stack.Split(" <- ").Length, 1, 40);
        Assert.Single(logger.Lines, line => line.Contains("uifreeze managed UI stack:"));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void KnownSixSecondUiBlock(string prompt)
    {
        Thread.Sleep(6000);
        GC.KeepAlive(prompt);
    }

    private sealed class CapturingLogger : ILogger<DiagnosticsBackgroundService>
    {
        public ConcurrentQueue<string> Lines { get; } = new();
        public TaskCompletionSource<string> Stack { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var line = formatter(state, exception);
            Lines.Enqueue(line);
            if (line.Contains("uifreeze managed UI stack:", StringComparison.Ordinal))
            {
                Stack.TrySetResult(line);
            }
        }
    }
}

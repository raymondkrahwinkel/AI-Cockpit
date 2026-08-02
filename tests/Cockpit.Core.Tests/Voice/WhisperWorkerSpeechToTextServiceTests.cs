using System.Diagnostics;
using System.Reflection;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;
using Cockpit.Infrastructure.Voice;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cockpit.Core.Tests.Voice;

/// <summary>
/// The idle-reaper timer callback (AC-533) and the exited-worker stderr tail (AC-534): a live dictation worker is
/// a real OS child process, so these tests reach into the private state via reflection rather than spawn one —
/// both are private callbacks that read/mutate state the public API never exposes directly. A never-started
/// <see cref="Process"/> stands in for "a worker exists"; killing it hits the same
/// <c>catch (Exception) { }</c> around <c>HasExited</c> that a raced-to-exit real worker would.
/// </summary>
public class WhisperWorkerSpeechToTextServiceTests
{
    [Fact]
    public void KillIfIdle_CalledTwiceWithNoDictationBetween_LogsAndKillsAtMostOnce()
    {
        var (service, logger, _) = _CreateService();
        _SetWorker(service, new Process());
        _SetLastUsedTicks(service, DateTime.UtcNow.AddMinutes(-10));

        _InvokeKillIfIdle(service);
        _InvokeKillIfIdle(service);

        Assert.Single(logger.Entries);
        Assert.Null(_GetWorker(service));
    }

    [Fact]
    public void KillIfIdle_NoWorkerLeft_LogsNothing()
    {
        var (service, logger, _) = _CreateService();
        _SetLastUsedTicks(service, DateTime.UtcNow.AddMinutes(-10));

        _InvokeKillIfIdle(service);

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void KillIfIdle_ClipInFlight_NeitherKillsNorLogs()
    {
        var (service, logger, _) = _CreateService();
        var worker = new Process();
        _SetWorker(service, worker);
        _SetPending(service, new TaskCompletionSource<string>());
        _SetLastUsedTicks(service, DateTime.UtcNow.AddMinutes(-10));

        _InvokeKillIfIdle(service);

        Assert.Empty(logger.Entries);
        Assert.Same(worker, _GetWorker(service));
    }

    [Fact]
    public void KillIfIdle_NotIdleYet_NeitherKillsNorLogs()
    {
        var (service, logger, _) = _CreateService();
        var worker = new Process();
        _SetWorker(service, worker);
        _SetLastUsedTicks(service, DateTime.UtcNow);

        _InvokeKillIfIdle(service);

        Assert.Empty(logger.Entries);
        Assert.Same(worker, _GetWorker(service));
    }

    [Fact]
    public async Task OnWorkerExited_StderrHasLines_FaultsBothWaitersWithTheTailFolded_In()
    {
        var (service, _, _) = _CreateService();
        var ready = new TaskCompletionSource();
        var pending = new TaskCompletionSource<string>();
        _Field(service, "_ready").SetValue(service, ready);
        _Field(service, "_pending").SetValue(service, pending);
        var stderrTail = new ProcessStderrTail();
        stderrTail.OnLine("CUDA out of memory");

        _Method(service, "_OnWorkerExited").Invoke(service, [stderrTail]);

        var readyError = await Assert.ThrowsAsync<InvalidOperationException>(() => ready.Task);
        var pendingError = await Assert.ThrowsAsync<InvalidOperationException>(() => pending.Task);
        Assert.Contains("exited unexpectedly", readyError.Message, StringComparison.Ordinal);
        Assert.Contains("CUDA out of memory", readyError.Message, StringComparison.Ordinal);
        Assert.Contains("CUDA out of memory", pendingError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnWorkerExited_NoStderrCaptured_MessageNamesNoTail()
    {
        var (service, _, _) = _CreateService();
        var ready = new TaskCompletionSource();
        _Field(service, "_ready").SetValue(service, ready);

        _Method(service, "_OnWorkerExited").Invoke(service, [new ProcessStderrTail()]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ready.Task);
        Assert.DoesNotContain("Stderr tail", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LogTranscribed_SuccessfulClip_LogsOneInformationLine_NeverAWarning()
    {
        var (service, logger, _) = _CreateService();

        _Method(service, "_LogTranscribed").Invoke(service, [3.2, 450L, 380L, true, 17]);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        var message = Assert.Single(logger.Messages);
        Assert.Contains("3.2", message, StringComparison.Ordinal);
        Assert.Contains("450", message, StringComparison.Ordinal);
        Assert.Contains("True", message, StringComparison.Ordinal);
        Assert.Contains("17", message, StringComparison.Ordinal);
        // The startup cost is reported on its own, not folded into the total: on a cold start it is most of it,
        // and a single number cannot tell "the machine is slow" apart from "the worker had to come up first".
        Assert.Contains("380", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WarmUp_CountsAsUse_SoTheReaperCannotKillWhatThePressJustWarmed()
    {
        // The press arrives minutes after the last clip, so the idle clock is already past its deadline. Warming
        // without touching it hands the next tick a worker that came up seconds ago and reads as long idle —
        // killed somewhere between this press and its release, which is the one moment it is needed.
        var (service, _, _) = _CreateService();
        _SetLastUsedTicks(service, DateTime.UtcNow.AddMinutes(-10));

        await service.WarmUpAsync();

        var worker = new Process();
        _SetWorker(service, worker);
        _InvokeKillIfIdle(service);

        Assert.Same(worker, _GetWorker(service));
    }

    [Fact]
    public async Task Spawning_IsHeldOnItsOwnGate_SoASecondPressCannotKillTheWorkerTheFirstIsWaitingOn()
    {
        // AC-603's race in one assertion: the spawn holds _spawnGate for its whole duration, so the release path
        // (which holds only _gate) queues behind it instead of walking past the health check, killing the loading
        // worker and starting the cold start again.
        var (service, _, settingsStore) = _CreateService();
        var insideLoad = new TaskCompletionSource();
        var finishLoad = new TaskCompletionSource<VoiceSettings>();
        settingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            insideLoad.TrySetResult();
            return finishLoad.Task;
        });

        var warming = service.WarmUpAsync();
        // A failure form, not a wait: the spawn either reaches the settings load or the test says so (AC-590).
        await insideLoad.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, _SpawnGate(service).CurrentCount);

        finishLoad.TrySetException(new InvalidOperationException("no worker is spawned in a test"));
        await warming;

        Assert.Equal(1, _SpawnGate(service).CurrentCount);
    }

    private static (WhisperWorkerSpeechToTextService Service, CapturingLogger<WhisperWorkerSpeechToTextService> Logger, IVoiceSettingsStore SettingsStore) _CreateService()
    {
        var settingsStore = Substitute.For<IVoiceSettingsStore>();
        var logger = new CapturingLogger<WhisperWorkerSpeechToTextService>();
        return (new WhisperWorkerSpeechToTextService(settingsStore, logger), logger, settingsStore);
    }

    private static SemaphoreSlim _SpawnGate(WhisperWorkerSpeechToTextService service) =>
        (SemaphoreSlim)_Field(service, "_spawnGate").GetValue(service)!;

    private static void _InvokeKillIfIdle(WhisperWorkerSpeechToTextService service) =>
        _Method(service, "_KillIfIdle").Invoke(service, null);

    private static void _SetWorker(WhisperWorkerSpeechToTextService service, Process? worker) =>
        _Field(service, "_worker").SetValue(service, worker);

    private static Process? _GetWorker(WhisperWorkerSpeechToTextService service) =>
        (Process?)_Field(service, "_worker").GetValue(service);

    private static void _SetPending(WhisperWorkerSpeechToTextService service, TaskCompletionSource<string>? pending) =>
        _Field(service, "_pending").SetValue(service, pending);

    private static void _SetLastUsedTicks(WhisperWorkerSpeechToTextService service, DateTime utc) =>
        _Field(service, "_lastUsedTicks").SetValue(service, utc.Ticks);

    private static FieldInfo _Field(WhisperWorkerSpeechToTextService service, string name) =>
        service.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingFieldException(nameof(WhisperWorkerSpeechToTextService), name);

    private static MethodInfo _Method(WhisperWorkerSpeechToTextService service, string name) =>
        service.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingMethodException(nameof(WhisperWorkerSpeechToTextService), name);
}

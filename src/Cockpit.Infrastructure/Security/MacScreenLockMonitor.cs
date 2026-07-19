using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Secrets;

namespace Cockpit.Infrastructure.Security;

/// <summary>
/// Watches macOS for screen lock/unlock via CoreFoundation's distributed notification center (AC-5): the system-wide
/// <c>com.apple.screenIsLocked</c> / <c>com.apple.screenIsUnlocked</c> notifications, the same source 1Password and
/// Slack use for this. These names are observable-stable rather than a published Apple API — decades unchanged, but
/// undocumented; the research covers that tradeoff, which is acceptable for Cockpit's distribution. <c>screenIsLocked</c>
/// also fires on screensaver/sleep, i.e. slightly more eagerly than a strict session lock — which for "re-ask for the
/// password" is the safe direction.
/// <para>
/// A distributed observer is delivered on the run loop of the thread that added it, so this runs a dedicated thread
/// whose only job is <c>CFRunLoopRun</c>. There is no Mac here to live-verify on, so this is written to the standard
/// P/Invoke pattern and kept thin; the gate that decides what a lock means lives in the testable coordinator above.
/// </para>
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacScreenLockMonitor(ILogger<MacScreenLockMonitor> logger) : IScreenLockMonitor
{
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint Utf8 = 0x08000100; // kCFStringEncodingUTF8
    private const int DeliverImmediately = 4; // CFNotificationSuspensionBehaviorDeliverImmediately

    // Held as a field so the GC never collects the delegate while native code still holds the function pointer.
    private CFNotificationCallback? _callback;
    private Thread? _runLoopThread;
    private IntPtr _runLoop;
    private IntPtr _lockedName;
    private IntPtr _unlockedName;

    public event EventHandler? Locked;

    public event EventHandler? Unlocked;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_runLoopThread is not null)
        {
            return Task.CompletedTask;
        }

        _callback = OnNotification;

        // The observer and the run loop must live on one thread — a background thread of our own, so the cockpit's UI
        // run loop is never blocked and the notifications still have a running loop to be delivered on.
        _runLoopThread = new Thread(RunLoop)
        {
            IsBackground = true,
            Name = "cockpit-macos-screenlock",
        };
        _runLoopThread.Start();

        return Task.CompletedTask;
    }

    private void RunLoop()
    {
        try
        {
            _runLoop = CFRunLoopGetCurrent();
            var center = CFNotificationCenterGetDistributedCenter();
            _lockedName = CFStringCreateWithCString(IntPtr.Zero, "com.apple.screenIsLocked", Utf8);
            _unlockedName = CFStringCreateWithCString(IntPtr.Zero, "com.apple.screenIsUnlocked", Utf8);

            CFNotificationCenterAddObserver(center, IntPtr.Zero, _callback!, _lockedName, IntPtr.Zero, DeliverImmediately);
            CFNotificationCenterAddObserver(center, IntPtr.Zero, _callback!, _unlockedName, IntPtr.Zero, DeliverImmediately);

            logger.LogInformation("Screen-lock detection registered via the macOS distributed notification center.");

            // Blocks here delivering notifications until CFRunLoopStop is called from Dispose.
            CFRunLoopRun();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Screen-lock detection is unavailable via CoreFoundation; the cockpit will not lock with the OS on this machine.");
        }
    }

    private void OnNotification(IntPtr center, IntPtr observer, IntPtr name, IntPtr obj, IntPtr userInfo)
    {
        if (name != IntPtr.Zero && _unlockedName != IntPtr.Zero && CFEqual(name, _unlockedName))
        {
            Unlocked?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            // Any of the lock-family notifications we registered for — treat as "locked" and let the coordinator's
            // idempotence collapse a screensaver-then-lock pair into one.
            Locked?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (_runLoop != IntPtr.Zero)
        {
            var center = CFNotificationCenterGetDistributedCenter();
            CFNotificationCenterRemoveEveryObserver(center, IntPtr.Zero);
            CFRunLoopStop(_runLoop);
            _runLoop = IntPtr.Zero;
        }

        if (_lockedName != IntPtr.Zero)
        {
            CFRelease(_lockedName);
            _lockedName = IntPtr.Zero;
        }

        if (_unlockedName != IntPtr.Zero)
        {
            CFRelease(_unlockedName);
            _unlockedName = IntPtr.Zero;
        }

        _runLoopThread = null;
        _callback = null;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CFNotificationCallback(IntPtr center, IntPtr observer, IntPtr name, IntPtr obj, IntPtr userInfo);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFNotificationCenterGetDistributedCenter();

    [DllImport(CoreFoundation)]
    private static extern void CFNotificationCenterAddObserver(
        IntPtr center,
        IntPtr observer,
        CFNotificationCallback callback,
        IntPtr name,
        IntPtr obj,
        int suspensionBehavior);

    [DllImport(CoreFoundation)]
    private static extern void CFNotificationCenterRemoveEveryObserver(IntPtr center, IntPtr observer);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr alloc, string cStr, uint encoding);

    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFEqual(IntPtr cf1, IntPtr cf2);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr cf);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFRunLoopGetCurrent();

    [DllImport(CoreFoundation)]
    private static extern void CFRunLoopRun();

    [DllImport(CoreFoundation)]
    private static extern void CFRunLoopStop(IntPtr runLoop);
}

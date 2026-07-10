using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

// SPIKE driver for route (a): "native-terminal keystroke inject".
//
// Two distinct mechanisms are tested, because they have very different
// automatability:
//
//   mode "attach"    -> AttachConsole(pid) + WriteConsoleInput into a
//                       console process WE spawned ourselves. This is
//                       fully automatable headlessly (no real window focus
//                       needed) because we own the child and can attach to
//                       its console programmatically. Proves whether
//                       WriteConsoleInput reaches a real interactive
//                       console's input buffer and gets processed by the
//                       shell as if typed.
//
//   mode "sendinput" -> classic SendInput() targeting whatever window has
//                       OS focus. This CANNOT be meaningfully validated by
//                       this headless agent: it requires a real, focused,
//                       on-screen terminal window in Raymond's desktop
//                       session, and success/failure must be visually
//                       confirmed by him. This spike only wires up the
//                       mechanism and prints clear manual-verification
//                       instructions; it does not claim success.
//
// This is throwaway spike code - minimal error handling.

string mode = args.Length > 0 ? args[0] : "attach";

if (mode == "attach")
{
    RunAttachConsoleTest();
}
else if (mode == "sendinput")
{
    RunSendInputManualInstructions();
}
else if (mode == "sendinput-fire")
{
    RunSendInputFire();
}
else
{
    Console.WriteLine("Unknown mode. Use 'attach', 'sendinput', or 'sendinput-fire'.");
}

static void RunAttachConsoleTest()
{
    string logPath = Path.Combine(Directory.GetCurrentDirectory(), "attach-test-result.log");
    void Log(string s)
    {
        Console.WriteLine(s); // best-effort; may go nowhere after FreeConsole()
        File.AppendAllText(logPath, s + Environment.NewLine);
    }

    if (File.Exists(logPath)) File.Delete(logPath);
    Log("=== Route (a) probe 1: AttachConsole + WriteConsoleInput into an owned console child ===");

    string marker = Path.Combine(Directory.GetCurrentDirectory(), "attach-inject-proof.txt");
    if (File.Exists(marker)) File.Delete(marker);

    // Spawn cmd.exe with its OWN real console window (new console, not
    // inherited), so it has a genuine console input buffer we can attach
    // to and feed via WriteConsoleInput - distinct from the ConPTY approach
    // in spike-input-pty, and from SendInput which needs OS window focus.
    var psi = new ProcessStartInfo
    {
        FileName = "cmd.exe",
        UseShellExecute = true,
        CreateNoWindow = false,
        WorkingDirectory = Directory.GetCurrentDirectory(),
    };
    var proc = Process.Start(psi)!;
    Log($"Spawned cmd.exe PID={proc.Id} with its own console window.");

    Thread.Sleep(1500); // let its console + shell banner initialize

    // AttachConsole fails with ERROR_ACCESS_DENIED (5) if the CALLER already
    // owns a console (which our own process does, launched from a shell).
    // Must FreeConsole() our own first - documented AttachConsole gotcha.
    NativeMethods.FreeConsole();

    if (!NativeMethods.AttachConsole((uint)proc.Id))
    {
        int err = Marshal.GetLastWin32Error();
        Log($"[RESULT] AttachConsole FAILED even after FreeConsole(), GetLastError={err}. Cannot proceed with WriteConsoleInput probe.");
        return;
    }
    Log("AttachConsole succeeded (after freeing our own console).");

    // GetStdHandle(STD_INPUT_HANDLE) proved unreliable here (returned a
    // handle that WriteConsoleInput rejected with ERROR_INVALID_HANDLE).
    // Open CONIN$ directly instead - the documented way to get a real
    // handle to the attached console's input buffer regardless of the
    // calling process's own (possibly confused) standard handle state.
    IntPtr hStdIn = NativeMethods.CreateFileW(
        "CONIN$",
        NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
        NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
        IntPtr.Zero,
        NativeMethods.OPEN_EXISTING,
        0,
        IntPtr.Zero);
    if (hStdIn == IntPtr.Zero || hStdIn == new IntPtr(-1))
    {
        Log($"[RESULT] CreateFileW(CONIN$) failed, GetLastError={Marshal.GetLastWin32Error()}");
        NativeMethods.FreeConsole();
        return;
    }
    Log($"CreateFileW(CONIN$) succeeded, handle={hStdIn}");

    string command = $"echo INJECTED_VIA_WRITECONSOLEINPUT > \"{marker}\"\r\n";
    var records = BuildKeyEventRecords(command);

    bool ok = NativeMethods.WriteConsoleInput(hStdIn, records, (uint)records.Length, out uint written);
    int writeErr = ok ? 0 : Marshal.GetLastWin32Error();
    Log($"WriteConsoleInput ok={ok}, eventsWritten={written}/{records.Length}, GetLastError={writeErr}, hStdIn={hStdIn}");

    // Detach from the console we attached to; give the child time to
    // process the injected input and act on it.
    NativeMethods.FreeConsole();
    Thread.Sleep(1500);

    if (File.Exists(marker))
    {
        Log($"[RESULT] PASS: marker created via WriteConsoleInput. Contents: {File.ReadAllText(marker).Trim()}");
    }
    else
    {
        Log("[RESULT] FAIL: marker not created - injected console input did not execute in the shell.");
    }

    try { if (!proc.HasExited) proc.Kill(); } catch { /* spike cleanup, best effort */ }
}

static void RunSendInputManualInstructions()
{
    Console.WriteLine("=== Route (a) probe 2: SendInput to focused window - MANUAL VERIFICATION REQUIRED ===");
    Console.WriteLine();
    Console.WriteLine("This headless agent session cannot bring a real terminal window into OS focus");
    Console.WriteLine("on Raymond's desktop, nor visually confirm the result. SendInput() posts");
    Console.WriteLine("keystrokes to whatever window currently has focus, system-wide - there is no");
    Console.WriteLine("target-window parameter. This is fundamentally a manual/interactive-only test.");
    Console.WriteLine();
    Console.WriteLine("To test manually:");
    Console.WriteLine("  1. Open a normal Windows Terminal / cmd / PowerShell window.");
    Console.WriteLine("  2. Run: NativeInjectSpike.exe sendinput-fire");
    Console.WriteLine("  3. Within the 3s countdown, click into the terminal window to focus it.");
    Console.WriteLine("  4. Observe whether 'echo INJECTED_VIA_SENDINPUT' + Enter appears and runs.");
}

static void RunSendInputFire()
{
    Console.WriteLine("Arming SendInput in 3 seconds - click into the target terminal window NOW...");
    for (int i = 3; i >= 1; i--)
    {
        Console.WriteLine(i);
        Thread.Sleep(1000);
    }

    string text = "echo INJECTED_VIA_SENDINPUT\r";
    var inputs = new System.Collections.Generic.List<NativeMethods.INPUT>();
    foreach (char c in text)
    {
        inputs.Add(MakeUnicodeKeyInput(c, keyUp: false));
        inputs.Add(MakeUnicodeKeyInput(c, keyUp: true));
    }
    var arr = inputs.ToArray();
    uint sent = NativeMethods.SendInput((uint)arr.Length, arr, Marshal.SizeOf<NativeMethods.INPUT>());
    Console.WriteLine($"SendInput reported {sent}/{arr.Length} events sent.");
    Console.WriteLine("Manually verify in the focused terminal window whether the command appeared and ran.");
}

static NativeMethods.INPUT MakeUnicodeKeyInput(char c, bool keyUp)
{
    return new NativeMethods.INPUT
    {
        type = NativeMethods.INPUT_KEYBOARD,
        U = new NativeMethods.InputUnion
        {
            ki = new NativeMethods.KEYBDINPUT
            {
                wVk = 0,
                wScan = c,
                dwFlags = NativeMethods.KEYEVENTF_UNICODE | (keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0),
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            }
        }
    };
}

static NativeMethods.INPUT_RECORD[] BuildKeyEventRecords(string text)
{
    var list = new System.Collections.Generic.List<NativeMethods.INPUT_RECORD>();
    foreach (char c in text)
    {
        list.Add(new NativeMethods.INPUT_RECORD
        {
            EventType = NativeMethods.KEY_EVENT,
            KeyEvent = new NativeMethods.KEY_EVENT_RECORD
            {
                bKeyDown = true,
                wRepeatCount = 1,
                wVirtualKeyCode = 0,
                wVirtualScanCode = 0,
                UnicodeChar = c,
                dwControlKeyState = 0,
            }
        });
        list.Add(new NativeMethods.INPUT_RECORD
        {
            EventType = NativeMethods.KEY_EVENT,
            KeyEvent = new NativeMethods.KEY_EVENT_RECORD
            {
                bKeyDown = false,
                wRepeatCount = 1,
                wVirtualKeyCode = 0,
                wVirtualScanCode = 0,
                UnicodeChar = c,
                dwControlKeyState = 0,
            }
        });
    }
    return list.ToArray();
}

internal static class NativeMethods
{
    public const uint STD_INPUT_HANDLE = unchecked(0xFFFFFFF6);
    public const ushort KEY_EVENT = 0x0001;
    public const uint INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_UNICODE = 0x0004;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint OPEN_EXISTING = 3;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetStdHandle(uint nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool WriteConsoleInput(IntPtr hConsoleInput, [In] INPUT_RECORD[] lpBuffer, uint nLength, out uint lpNumberOfEventsWritten);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    public struct KEY_EVENT_RECORD
    {
        [MarshalAs(UnmanagedType.Bool)] public bool bKeyDown;
        public ushort wRepeatCount;
        public ushort wVirtualKeyCode;
        public ushort wVirtualScanCode;
        public char UnicodeChar;
        public uint dwControlKeyState;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUT_RECORD
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public KEY_EVENT_RECORD KeyEvent;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion U;
    }
}

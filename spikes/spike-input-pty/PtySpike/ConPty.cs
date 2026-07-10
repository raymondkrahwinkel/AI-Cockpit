// Minimal ConPTY wrapper via raw P/Invoke of CreatePseudoConsole.
// Adapted from Microsoft's official ConPTY sample pattern
// (docs.microsoft.com/windows/console/creating-a-pseudoconsole-session).
// SPIKE CODE - throwaway, not hardened. No cleanup-on-exception rigor,
// minimal error handling beyond what's needed to prove the mechanism.
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PtySpike;

internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    public struct COORD
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    public const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    public const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll")]
    public static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr Attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll")]
    public static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CreateProcessW(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);
}

/// <summary>
/// Hosts a child process inside a Windows ConPTY (pseudo console) so it
/// believes it's attached to a real interactive terminal. Exposes the
/// PTY's input pipe (write here to send "keystrokes") and output pipe
/// (read here to get rendered terminal output, ANSI and all).
/// </summary>
public sealed class ConPtyProcess : IDisposable
{
    private IntPtr _hPC;
    private IntPtr _attrListBuffer;
    private NativeMethods.PROCESS_INFORMATION _procInfo;
    private SafeFileHandle? _ptyInWrite;   // we write "keystrokes" here
    private SafeFileHandle? _ptyOutRead;   // we read rendered output here

    public FileStream InputWriter { get; private set; } = null!;
    public FileStream OutputReader { get; private set; } = null!;
    public int ProcessId => _procInfo.dwProcessId;

    public static ConPtyProcess Start(string commandLine, string? workingDirectory, short cols = 120, short rows = 30)
    {
        var self = new ConPtyProcess();
        self.StartInternal(commandLine, workingDirectory, cols, rows);
        return self;
    }

    private void StartInternal(string commandLine, string? workingDirectory, short cols, short rows)
    {
        // Pipe pair for PTY's stdin: we write to inWrite, ConPTY reads from inRead.
        if (!NativeMethods.CreatePipe(out var inRead, out var inWrite, IntPtr.Zero, 0))
            throw new InvalidOperationException("CreatePipe (input) failed: " + Marshal.GetLastWin32Error());

        // Pipe pair for PTY's stdout: ConPTY writes to outWrite, we read from outRead.
        if (!NativeMethods.CreatePipe(out var outRead, out var outWrite, IntPtr.Zero, 0))
            throw new InvalidOperationException("CreatePipe (output) failed: " + Marshal.GetLastWin32Error());

        var size = new NativeMethods.COORD { X = cols, Y = rows };
        int hr = NativeMethods.CreatePseudoConsole(size, inRead, outWrite, 0, out _hPC);
        if (hr != 0)
            throw new InvalidOperationException($"CreatePseudoConsole failed, hresult=0x{hr:X}");

        // Once ConPTY owns these ends, we can close our copies of the "other side" handles.
        inRead.Close();
        outWrite.Close();

        _ptyInWrite = inWrite;
        _ptyOutRead = outRead;
        InputWriter = new FileStream(inWrite, FileAccess.Write);
        OutputReader = new FileStream(outRead, FileAccess.Read);

        // Build the attribute list that attaches the pseudoconsole to the child process.
        IntPtr attrListSize = IntPtr.Zero;
        NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrListSize);
        _attrListBuffer = Marshal.AllocHGlobal(attrListSize);
        if (!NativeMethods.InitializeProcThreadAttributeList(_attrListBuffer, 1, 0, ref attrListSize))
            throw new InvalidOperationException("InitializeProcThreadAttributeList failed: " + Marshal.GetLastWin32Error());

        if (!NativeMethods.UpdateProcThreadAttribute(
                _attrListBuffer,
                0,
                (IntPtr)NativeMethods.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                _hPC,
                (IntPtr)IntPtr.Size,
                IntPtr.Zero,
                IntPtr.Zero))
            throw new InvalidOperationException("UpdateProcThreadAttribute failed: " + Marshal.GetLastWin32Error());

        var startupInfoEx = new NativeMethods.STARTUPINFOEX
        {
            StartupInfo = new NativeMethods.STARTUPINFO(),
            lpAttributeList = _attrListBuffer,
        };
        // NOTE: sample docs say "sizeof(STARTUPINFOEX)" - Marshal.SizeOf on the
        // managed struct should match the native layout since both fields are
        // blittable (struct + IntPtr), but this is exactly the kind of subtle
        // marshaling detail worth double-checking if capture keeps failing.
        startupInfoEx.StartupInfo.cb = Marshal.SizeOf<NativeMethods.STARTUPINFOEX>();

        bool ok = NativeMethods.CreateProcessW(
            null,
            commandLine,
            IntPtr.Zero,
            IntPtr.Zero,
            false,
            NativeMethods.EXTENDED_STARTUPINFO_PRESENT,
            IntPtr.Zero,
            workingDirectory,
            ref startupInfoEx,
            out _procInfo);

        if (!ok)
            throw new InvalidOperationException("CreateProcessW failed: " + Marshal.GetLastWin32Error());
    }

    public void Resize(short cols, short rows)
    {
        NativeMethods.ResizePseudoConsole(_hPC, new NativeMethods.COORD { X = cols, Y = rows });
    }

    public void Dispose()
    {
        try { InputWriter?.Dispose(); } catch { /* spike: best-effort cleanup */ }
        try { OutputReader?.Dispose(); } catch { /* spike: best-effort cleanup */ }
        if (_hPC != IntPtr.Zero) NativeMethods.ClosePseudoConsole(_hPC);
        if (_attrListBuffer != IntPtr.Zero)
        {
            NativeMethods.DeleteProcThreadAttributeList(_attrListBuffer);
            Marshal.FreeHGlobal(_attrListBuffer);
        }
        if (_procInfo.hProcess != IntPtr.Zero) NativeMethods.CloseHandle(_procInfo.hProcess);
        if (_procInfo.hThread != IntPtr.Zero) NativeMethods.CloseHandle(_procInfo.hThread);
    }
}

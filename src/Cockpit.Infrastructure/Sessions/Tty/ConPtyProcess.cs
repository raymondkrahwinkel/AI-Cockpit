using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Cockpit.Core.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions.Tty;

/// <summary>
/// Hosts a child process inside a Windows ConPTY (pseudo console) via <c>CreatePseudoConsole</c>,
/// so the child sees a real interactive terminal. Exposes the pty's input pipe (write keystrokes)
/// and output pipe (read rendered ANSI/VT output), and forwards resizes to <c>ResizePseudoConsole</c>.
/// </summary>
/// <remarks>
/// The child receives exactly the environment we build (ConPTY has no implicit inheritance), passed
/// as a UTF-16 double-null-terminated block with <c>CREATE_UNICODE_ENVIRONMENT</c>. This is the whole
/// reason the cockpit hosts the pty itself rather than using a turnkey terminal control: it is the
/// only way to inject <c>CLAUDE_CONFIG_DIR</c> and <c>TERM</c> alongside the inherited parent env.
///
/// Windows-only by construction (P/Invokes kernel32 ConPTY, available Windows 10 1809+). The
/// Linux/macOS counterpart is <see cref="PortaPtyProcess"/> (Porta.Pty); <see cref="ConPtyHostFactory"/>
/// and <see cref="PortaPtyHostFactory"/> are selected per platform behind <c>IPtyHostFactory</c>.
/// </remarks>
internal sealed class ConPtyProcess : IConPtyProcess
{
    private const int ProcThreadAttributePseudoConsole = 0x00020016;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;

    private IntPtr _pseudoConsole;
    private IntPtr _attributeList;
    private ProcessInformation _processInfo;
    private readonly SafeFileHandle _inputWriteHandle;
    private readonly SafeFileHandle _outputReadHandle;

    // Guards _pseudoConsole against a double close: it is closed either when the child exits (the wait
    // callback below) or on Dispose, whichever comes first, and both run on unrelated threads.
    private readonly object _closeLock = new();

    // Watches the child process handle so the pseudo console is closed the moment the child exits. This
    // is the whole Windows-only fix for the hung TTY panel: unlike a Unix pty master (which EOFs its
    // reader when the child dies), ConPTY keeps the output pipe's write end open after the child exits,
    // so a reader would block forever and the panel would never learn the process is gone. Closing the
    // pseudo console on exit signals that EOF, exactly as the Unix side gets for free.
    private RegisteredWaitHandle? _exitWait;
    private readonly ProcessWaitHandle? _processWaitHandle;

    public Stream InputStream { get; }

    public Stream OutputStream { get; }

    public int ProcessId => _processInfo.ProcessId;

    private ConPtyProcess(
        SafeFileHandle inputWriteHandle,
        SafeFileHandle outputReadHandle,
        IntPtr pseudoConsole,
        IntPtr attributeList,
        ProcessInformation processInfo)
    {
        _inputWriteHandle = inputWriteHandle;
        _outputReadHandle = outputReadHandle;
        _pseudoConsole = pseudoConsole;
        _attributeList = attributeList;
        _processInfo = processInfo;
        InputStream = new FileStream(inputWriteHandle, FileAccess.Write);
        OutputStream = new FileStream(outputReadHandle, FileAccess.Read);

        // A Windows process handle becomes signaled when the process terminates; wait on it and close the
        // pseudo console (EOF to the reader) when it fires. ownsHandle:false — Dispose owns _processInfo.Process
        // and closes it itself, after unregistering this wait.
        _processWaitHandle = new ProcessWaitHandle(processInfo.Process);
        _exitWait = ThreadPool.RegisterWaitForSingleObject(
            _processWaitHandle, OnChildExited, null, Timeout.Infinite, executeOnlyOnce: true);
    }

    /// <summary>
    /// Fired by the thread pool once the child process terminates. Closes the pseudo console so the
    /// output pipe reaches EOF and the output pump stops (which is how the panel learns to close). On
    /// Windows this is the only signal of the exit — the reader never sees EOF on its own while ConPTY
    /// holds the pipe's write end open.
    /// </summary>
    private void OnChildExited(object? state, bool timedOut) => ClosePseudoConsole();

    private void ClosePseudoConsole()
    {
        lock (_closeLock)
        {
            if (_pseudoConsole != IntPtr.Zero)
            {
                // Closing the pseudo console signals EOF to the child and to our output pipe reader.
                NativeMethods.ClosePseudoConsole(_pseudoConsole);
                _pseudoConsole = IntPtr.Zero;
            }
        }
    }

    /// <summary>
    /// Spawns <paramref name="commandLine"/> inside a fresh pseudo console of the given size, in
    /// <paramref name="workingDirectory"/>, with exactly <paramref name="environment"/> as its
    /// environment block.
    /// </summary>
    public static ConPtyProcess Start(
        string commandLine,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        short columns,
        short rows)
    {
        if (!NativeMethods.CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0))
        {
            throw new InvalidOperationException($"CreatePipe (input) failed: {Marshal.GetLastWin32Error()}");
        }

        if (!NativeMethods.CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0))
        {
            inputRead.Dispose();
            inputWrite.Dispose();
            throw new InvalidOperationException($"CreatePipe (output) failed: {Marshal.GetLastWin32Error()}");
        }

        var size = new Coord { X = columns, Y = rows };
        var hr = NativeMethods.CreatePseudoConsole(size, inputRead, outputWrite, 0, out var pseudoConsole);

        // ConPTY has cloned the ends it needs; our copies of the "other side" are no longer required.
        inputRead.Dispose();
        outputWrite.Dispose();

        if (hr != 0)
        {
            inputWrite.Dispose();
            outputRead.Dispose();
            throw new InvalidOperationException($"CreatePseudoConsole failed, hresult=0x{hr:X}.");
        }

        var attributeList = BuildPseudoConsoleAttributeList(pseudoConsole);

        var startupInfo = new StartupInfoEx
        {
            StartupInfo = { cb = Marshal.SizeOf<StartupInfoEx>() },
            AttributeList = attributeList,
        };

        var environmentBlock = BuildEnvironmentBlock(environment);
        var environmentHandle = GCHandle.Alloc(environmentBlock, GCHandleType.Pinned);
        try
        {
            var started = NativeMethods.CreateProcessW(
                null,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                bInheritHandles: false,
                ExtendedStartupInfoPresent | CreateUnicodeEnvironment,
                environmentHandle.AddrOfPinnedObject(),
                string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory,
                ref startupInfo,
                out var processInfo);

            if (!started)
            {
                var error = Marshal.GetLastWin32Error();
                NativeMethods.DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
                NativeMethods.ClosePseudoConsole(pseudoConsole);
                inputWrite.Dispose();
                outputRead.Dispose();
                throw new InvalidOperationException($"CreateProcessW failed: {error}.");
            }

            return new ConPtyProcess(inputWrite, outputRead, pseudoConsole, attributeList, processInfo);
        }
        finally
        {
            environmentHandle.Free();
        }
    }

    public void Resize(short columns, short rows)
    {
        if (_pseudoConsole != IntPtr.Zero)
        {
            NativeMethods.ResizePseudoConsole(_pseudoConsole, new Coord { X = columns, Y = rows });
        }
    }

    public void Dispose()
    {
        // Stop watching the child before the process handle it waits on is closed below. Unregister does
        // not block here; the close is idempotent (guarded by _closeLock) if the callback fires concurrently.
        _exitWait?.Unregister(null);
        _exitWait = null;
        _processWaitHandle?.Dispose();

        InputStream.Dispose();
        OutputStream.Dispose();

        // Closing the pseudo console signals EOF to the child; it then exits on its own. Shared with the
        // exit-watch path so a child that is still running when the panel closes is torn down the same way.
        ClosePseudoConsole();

        if (_attributeList != IntPtr.Zero)
        {
            NativeMethods.DeleteProcThreadAttributeList(_attributeList);
            Marshal.FreeHGlobal(_attributeList);
            _attributeList = IntPtr.Zero;
        }

        if (_processInfo.Process != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_processInfo.Process);
            _processInfo.Process = IntPtr.Zero;
        }

        if (_processInfo.Thread != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_processInfo.Thread);
            _processInfo.Thread = IntPtr.Zero;
        }
    }

    private static IntPtr BuildPseudoConsoleAttributeList(IntPtr pseudoConsole)
    {
        var listSize = IntPtr.Zero;
        NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref listSize);
        var attributeList = Marshal.AllocHGlobal(listSize);
        if (!NativeMethods.InitializeProcThreadAttributeList(attributeList, 1, 0, ref listSize))
        {
            Marshal.FreeHGlobal(attributeList);
            throw new InvalidOperationException($"InitializeProcThreadAttributeList failed: {Marshal.GetLastWin32Error()}");
        }

        if (!NativeMethods.UpdateProcThreadAttribute(
                attributeList,
                0,
                ProcThreadAttributePseudoConsole,
                pseudoConsole,
                IntPtr.Size,
                IntPtr.Zero,
                IntPtr.Zero))
        {
            NativeMethods.DeleteProcThreadAttributeList(attributeList);
            Marshal.FreeHGlobal(attributeList);
            throw new InvalidOperationException($"UpdateProcThreadAttribute failed: {Marshal.GetLastWin32Error()}");
        }

        return attributeList;
    }

    /// <summary>
    /// Builds a Windows Unicode environment block: <c>KEY=VALUE\0KEY=VALUE\0...\0</c> as UTF-16 bytes.
    /// Keys are sorted case-insensitively as the OS expects for an environment block.
    /// </summary>
    private static byte[] BuildEnvironmentBlock(IReadOnlyDictionary<string, string> environment)
    {
        var builder = new StringBuilder();
        foreach (var pair in environment.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
        }

        builder.Append('\0');
        return Encoding.Unicode.GetBytes(builder.ToString());
    }

    /// <summary>
    /// A <see cref="WaitHandle"/> over a raw Windows process handle, which the OS signals when the process
    /// terminates. Wraps the handle with <c>ownsHandle: false</c> so disposing this (on Dispose) never
    /// closes the process handle — <see cref="ConPtyProcess"/> owns it in <c>_processInfo.Process</c> and
    /// closes it separately after the wait is unregistered.
    /// </summary>
    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(IntPtr processHandle)
        {
            SafeWaitHandle = new SafeWaitHandle(processHandle, ownsHandle: false);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int cb;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2;
        public IntPtr Reserved2Pointer;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr pseudoConsole);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern int ResizePseudoConsole(IntPtr pseudoConsole, Coord size);

        [DllImport("kernel32.dll")]
        public static extern void ClosePseudoConsole(IntPtr pseudoConsole);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe, IntPtr pipeAttributes, int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool InitializeProcThreadAttributeList(IntPtr attributeList, int attributeCount, int flags, ref IntPtr size);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool UpdateProcThreadAttribute(IntPtr attributeList, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previousValue, IntPtr returnSize);

        [DllImport("kernel32.dll")]
        public static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateProcessW(
            string? applicationName,
            string commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            bool bInheritHandles,
            uint creationFlags,
            IntPtr environment,
            string? currentDirectory,
            ref StartupInfoEx startupInfo,
            out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}

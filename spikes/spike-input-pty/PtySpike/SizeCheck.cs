using System;
using System.Runtime.InteropServices;

namespace PtySpike;

public static class SizeCheck
{
    public static void Run()
    {
        Console.WriteLine("sizeof(STARTUPINFO)   = " + Marshal.SizeOf<NativeMethods.STARTUPINFO>());
        Console.WriteLine("sizeof(STARTUPINFOEX) = " + Marshal.SizeOf<NativeMethods.STARTUPINFOEX>());
        Console.WriteLine("IntPtr.Size            = " + IntPtr.Size);
        // Native STARTUPINFOEXW on x64 should be: STARTUPINFOW (104 bytes) + padding + LPPROC_THREAD_ATTRIBUTE_LIST (8 bytes) = 112 bytes typically.
    }
}

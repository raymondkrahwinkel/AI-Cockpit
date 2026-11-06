"""Observe CPU use of build executables without reading arguments or touching processes."""
import ctypes
from ctypes import wintypes

KERNEL = ctypes.WinDLL("kernel32", use_last_error=True)
KERNEL.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
KERNEL.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
KERNEL.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
KERNEL.OpenProcess.restype = wintypes.HANDLE
KERNEL.CloseHandle.argtypes = [wintypes.HANDLE]
KERNEL.GetProcessTimes.argtypes = [wintypes.HANDLE] + [ctypes.POINTER(wintypes.FILETIME)] * 4
BUILDERS = {"dotnet.exe", "msbuild.exe", "vbcscompiler.exe", "testhost.exe", "testhost.x86.exe", "vboxheadless.exe", "csc.exe", "cl.exe", "ninja.exe", "cmake.exe", "npm.exe", "pnpm.exe", "cargo.exe", "rustc.exe", "java.exe", "gradle.exe", "make.exe"}


class Entry(ctypes.Structure):
    _fields_ = [("size", wintypes.DWORD), ("usage", wintypes.DWORD), ("pid", wintypes.DWORD), ("heap", ctypes.c_size_t), ("module", wintypes.DWORD), ("threads", wintypes.DWORD), ("parent", wintypes.DWORD), ("priority", wintypes.LONG), ("flags", wintypes.DWORD), ("name", wintypes.WCHAR * 260)]


KERNEL.Process32FirstW.argtypes = [wintypes.HANDLE, ctypes.POINTER(Entry)]
KERNEL.Process32NextW.argtypes = [wintypes.HANDLE, ctypes.POINTER(Entry)]


def ticks(value: wintypes.FILETIME) -> int:
    return (value.dwHighDateTime << 32) | value.dwLowDateTime


def builders() -> dict[int, tuple[str, int, int]]:
    snapshot = KERNEL.CreateToolhelp32Snapshot(2, 0)
    if snapshot == ctypes.c_void_p(-1).value:
        raise ctypes.WinError(ctypes.get_last_error())
    result = {}
    try:
        entry = Entry()
        entry.size = ctypes.sizeof(entry)
        more = KERNEL.Process32FirstW(snapshot, ctypes.byref(entry))
        while more:
            if entry.name.lower() in BUILDERS:
                handle = KERNEL.OpenProcess(4096, False, entry.pid)
                if not handle:
                    raise RuntimeError(f"Cannot inspect builder PID {entry.pid}")
                try:
                    creation, exit_time, kernel, user = (wintypes.FILETIME() for _ in range(4))
                    if not KERNEL.GetProcessTimes(handle, ctypes.byref(creation), ctypes.byref(exit_time), ctypes.byref(kernel), ctypes.byref(user)):
                        raise ctypes.WinError(ctypes.get_last_error())
                    result[entry.pid] = (entry.name, ticks(creation), ticks(kernel) + ticks(user))
                finally:
                    KERNEL.CloseHandle(handle)
            more = KERNEL.Process32NextW(snapshot, ctypes.byref(entry))
    finally:
        KERNEL.CloseHandle(snapshot)
    return result


def activity(before: dict, after: dict) -> list[dict]:
    active = []
    for pid, (name, created, cpu) in after.items():
        prior = before.get(pid)
        delta = cpu - prior[2] if prior and prior[1] == created else cpu
        if delta > 0:
            active.append({"pid": pid, "name": name, "cpuMilliseconds": delta / 10000})
    for pid in before.keys() - after.keys():
        active.append({"pid": pid, "name": before[pid][0], "status": "exited-between-samples"})
    return active

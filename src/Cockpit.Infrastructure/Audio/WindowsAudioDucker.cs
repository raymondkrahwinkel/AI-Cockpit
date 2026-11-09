using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Audio;

[SupportedOSPlatform("windows")]
internal sealed class WindowsAudioDucker(ILogger<WindowsAudioDucker> logger, string journalPath)
    : JournaledAudioDucker(logger, journalPath)
{
    protected override Task<IReadOnlyList<AudioDuckingSession>> ReadSessionsAsync(CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<AudioDuckingSession>>(() =>
        {
            var sessions = new List<AudioDuckingSession>();
            _VisitSessions(null, (deviceId, control, volume) =>
            {
                control.GetProcessId(out uint processId);
                if (control.IsSystemSoundsSession() == 0 || processId == 0)
                {
                    return;
                }
                control.GetSessionInstanceIdentifier(out string id);
                control.GetSessionIdentifier(out string restoreId);
                control.GetState(out int state);
                volume.GetMasterVolume(out float current);
                sessions.Add(new AudioDuckingSession(deviceId, id, restoreId, checked((int)processId), state == 1, [current]));
            });
            return sessions;
        }, cancellationToken);

    protected override Task SetVolumesAsync(AudioDuckingSession session, double[] volumes, CancellationToken cancellationToken) =>
        Task.Run(() => _VisitSessions(session.DeviceId, (_, control, volume) =>
        {
            control.GetSessionInstanceIdentifier(out string id);
            control.GetProcessId(out uint processId);
            if (id == session.Id && processId == session.ProcessId && processId != Environment.ProcessId)
            {
                var context = Guid.Empty;
                volume.SetMasterVolume((float)volumes[0], ref context);
            }
        }), cancellationToken);

    private static void _VisitSessions(string? selectedDevice, Action<string, IAudioSessionControl2, ISimpleAudioVolume> visit)
    {
        int initialized = CoInitializeEx(IntPtr.Zero, 0);
        Marshal.ThrowExceptionForHR(initialized);
        try
        {
            var enumerator = (IMMDeviceEnumerator)(Activator.CreateInstance(
                Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), throwOnError: true)
                ?? throw new InvalidOperationException("Windows audio device enumeration is unavailable."))
                ?? throw new InvalidOperationException("Windows audio device enumeration could not start."));
            try
            {
                enumerator.EnumAudioEndpoints(0, 1, out IMMDeviceCollection devices);
                try
                {
                    devices.GetCount(out uint count);
                    for (uint index = 0; index < count; index++)
                    {
                        devices.Item(index, out IMMDevice device);
                        try
                        {
                            device.GetId(out string deviceId);
                            if (selectedDevice is null || deviceId == selectedDevice)
                            {
                                _VisitDevice(device, deviceId, visit);
                            }
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(device);
                        }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(devices);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }
        finally
        {
            CoUninitialize();
        }
    }

    private static void _VisitDevice(IMMDevice device, string deviceId, Action<string, IAudioSessionControl2, ISimpleAudioVolume> visit)
    {
        Guid managerId = typeof(IAudioSessionManager2).GUID;
        device.Activate(ref managerId, 23, IntPtr.Zero, out object managerObject);
        try
        {
            var manager = (IAudioSessionManager2)managerObject;
            manager.GetSessionEnumerator(out IAudioSessionEnumerator sessions);
            try
            {
                sessions.GetCount(out int count);
                for (int index = 0; index < count; index++)
                {
                    sessions.GetSession(index, out object session);
                    try
                    {
                        visit(deviceId, (IAudioSessionControl2)session, (ISimpleAudioVolume)session);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(session);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(sessions);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(managerObject);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint concurrency);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    // Unused slots preserve the native vtable layout (AC-717).
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int dataFlow, uint stateMask, out IMMDeviceCollection devices);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        void GetCount(out uint count);
        void Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(ref Guid id, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        void _OpenPropertyStore();
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        void _GetAudioSessionControl();
        void _GetSimpleAudioVolume();
        void GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        void GetCount(out int count);
        void GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        void GetState(out int state);
        void _GetDisplayName();
        void _SetDisplayName();
        void _GetIconPath();
        void _SetIconPath();
        void _GetGroupingParam();
        void _SetGroupingParam();
        void _RegisterAudioSessionNotification();
        void _UnregisterAudioSessionNotification();
        void GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetProcessId(out uint processId);
        [PreserveSig] int IsSystemSoundsSession();
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        void SetMasterVolume(float level, ref Guid context);
        void GetMasterVolume(out float level);
    }
}

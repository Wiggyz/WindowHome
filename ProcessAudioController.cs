using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MonitorApp;

public sealed class ProcessAudioController
{
    private const float VolumeStep = 0.05f;
    private static readonly Guid ClsIdMmDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IidIAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

    public void ApplyToMatchingSessions(SoundControlSettings settings, SoundHotkeyAction action)
    {
        if (settings.Rules.Count == 0)
        {
            return;
        }

        IMMDeviceEnumerator? deviceEnumerator = null;
        IMMDevice? device = null;
        object? sessionManagerObject = null;
        IAudioSessionManager2? sessionManager = null;
        IAudioSessionEnumerator? sessionEnumerator = null;

        try
        {
            deviceEnumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(ClsIdMmDeviceEnumerator)!)!;
            Marshal.ThrowExceptionForHR(deviceEnumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out device));
            var activateGuid = IidIAudioSessionManager2;
            Marshal.ThrowExceptionForHR(device!.Activate(ref activateGuid, 0, nint.Zero, out sessionManagerObject));
            sessionManager = (IAudioSessionManager2)sessionManagerObject!;
            Marshal.ThrowExceptionForHR(sessionManager.GetSessionEnumerator(out sessionEnumerator));
            Marshal.ThrowExceptionForHR(sessionEnumerator!.GetCount(out var count));

            for (var i = 0; i < count; i++)
            {
                IAudioSessionControl? sessionControl = null;
                IAudioSessionControl2? sessionControl2 = null;
                ISimpleAudioVolume? simpleVolume = null;
                try
                {
                    Marshal.ThrowExceptionForHR(sessionEnumerator.GetSession(i, out sessionControl));
                    sessionControl2 = (IAudioSessionControl2)sessionControl!;
                    simpleVolume = (ISimpleAudioVolume)sessionControl!;
                    Marshal.ThrowExceptionForHR(sessionControl2.GetProcessId(out var processId));
                    if (processId == 0 || !TryGetProcess(processId, out var process))
                    {
                        continue;
                    }

                    if (!settings.Rules.Any(rule => SoundControlAutomation.MatchesProcess(rule, process)))
                    {
                        continue;
                    }

                    ApplyAction(simpleVolume, action);
                }
                catch
                {
                }
                finally
                {
                    ReleaseComObject(simpleVolume);
                    ReleaseComObject(sessionControl2);
                    ReleaseComObject(sessionControl);
                }
            }
        }
        catch
        {
        }
        finally
        {
            ReleaseComObject(sessionEnumerator);
            ReleaseComObject(sessionManager);
            ReleaseComObject(sessionManagerObject);
            ReleaseComObject(device);
            ReleaseComObject(deviceEnumerator);
        }
    }

    private static void ApplyAction(ISimpleAudioVolume simpleVolume, SoundHotkeyAction action)
    {
        Marshal.ThrowExceptionForHR(simpleVolume.GetMasterVolume(out var currentVolume));
        switch (action)
        {
            case SoundHotkeyAction.VolumeUp:
                Marshal.ThrowExceptionForHR(simpleVolume.SetMasterVolume(Math.Clamp(currentVolume + VolumeStep, 0f, 1f), Guid.Empty));
                break;
            case SoundHotkeyAction.VolumeDown:
                Marshal.ThrowExceptionForHR(simpleVolume.SetMasterVolume(Math.Clamp(currentVolume - VolumeStep, 0f, 1f), Guid.Empty));
                break;
            case SoundHotkeyAction.ToggleMute:
                Marshal.ThrowExceptionForHR(simpleVolume.GetMute(out var mute));
                Marshal.ThrowExceptionForHR(simpleVolume.SetMute(!mute, Guid.Empty));
                break;
        }
    }

    private static bool TryGetProcess(int processId, out RunningProcessInfo process)
    {
        process = new RunningProcessInfo("", "");
        try
        {
            using var runningProcess = Process.GetProcessById(processId);
            var executablePath = "";
            try
            {
                executablePath = runningProcess.MainModule?.FileName ?? "";
            }
            catch
            {
                executablePath = "";
            }

            process = new RunningProcessInfo(runningProcess.ProcessName, executablePath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ReleaseComObject(object? instance)
    {
        if (instance is not null && Marshal.IsComObject(instance))
        {
            Marshal.ReleaseComObject(instance);
        }
    }

    private enum EDataFlow
    {
        eRender,
        eCapture,
        eAll
    }

    private enum ERole
    {
        eConsole,
        eMultimedia,
        eCommunications
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int NotImpl1();
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppDevice);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int dwClsCtx, nint pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    }

    [ComImport]
    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        int NotImpl0();
        int NotImpl1();
        int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
    }

    [ComImport]
    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        int GetCount(out int sessionCount);
        int GetSession(int sessionIndex, out IAudioSessionControl session);
    }

    [ComImport]
    [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        int NotImpl0();
        int NotImpl1();
        int NotImpl2();
        int NotImpl3();
        int NotImpl4();
        int NotImpl5();
        int NotImpl6();
        int NotImpl7();
        int NotImpl8();
        int NotImpl9();
    }

    [ComImport]
    [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        int NotImpl0();
        int NotImpl1();
        int NotImpl2();
        int NotImpl3();
        int NotImpl4();
        int NotImpl5();
        int NotImpl6();
        int NotImpl7();
        int NotImpl8();
        int NotImpl9();
        int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string retVal);
        int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string retVal);
        int GetProcessId(out int retv);
        int IsSystemSoundsSession();
        int SetDuckingPreference(bool optOut);
    }

    [ComImport]
    [Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        int SetMasterVolume(float level, Guid eventContext);
        int GetMasterVolume(out float level);
        int SetMute(bool isMuted, Guid eventContext);
        int GetMute(out bool isMuted);
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MonitorApp;

public sealed class GlobalHotkeyService : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonDown = 0x0204;
    private const int WmRButtonUp = 0x0205;
    private const int WmMButtonDown = 0x0207;
    private const int WmMButtonUp = 0x0208;
    private const int WmXButtonDown = 0x020B;
    private const int WmXButtonUp = 0x020C;
    private const int XButton1 = 1;
    private const int MouseLeftCode = 1;
    private const int MouseRightCode = 2;
    private const int MouseMiddleCode = 3;
    private const int MouseX1Code = 4;
    private const int MouseX2Code = 5;

    private readonly Func<SoundControlSettings> _settingsAccessor;
    private readonly Action<SoundHotkeyAction> _onTriggered;
    private readonly LowLevelProc _keyboardProc;
    private readonly LowLevelProc _mouseProc;
    private readonly HashSet<PressedInput> _pressedInputs = [];
    private nint _keyboardHook;
    private nint _mouseHook;

    public GlobalHotkeyService(Func<SoundControlSettings> settingsAccessor, Action<SoundHotkeyAction> onTriggered)
    {
        _settingsAccessor = settingsAccessor;
        _onTriggered = onTriggered;
        _keyboardProc = KeyboardHookCallback;
        _mouseProc = MouseHookCallback;
    }

    public void Start()
    {
        if (_keyboardHook != nint.Zero || _mouseHook != nint.Zero)
        {
            return;
        }

        var moduleHandle = GetModuleHandle(null);
        _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, moduleHandle, 0);
        _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, moduleHandle, 0);
        if (_keyboardHook == nint.Zero || _mouseHook == nint.Zero)
        {
            Stop();
            throw new InvalidOperationException("Could not install global sound-control hooks.");
        }
    }

    public void Stop()
    {
        if (_keyboardHook != nint.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = nint.Zero;
        }

        if (_mouseHook != nint.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = nint.Zero;
        }

        _pressedInputs.Clear();
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    private nint KeyboardHookCallback(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var message = unchecked((int)wParam);
            var keyInfo = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            var token = new PressedInput(HotkeyInputKind.Keyboard, (int)keyInfo.VirtualKeyCode);
            if (message is WmKeyDown or WmSysKeyDown)
            {
                if (_pressedInputs.Add(token))
                {
                    TriggerIfMatched(new HotkeyBinding { Kind = HotkeyInputKind.Keyboard, Code = (int)keyInfo.VirtualKeyCode });
                }
            }
            else if (message is WmKeyUp or WmSysKeyUp)
            {
                _pressedInputs.Remove(token);
            }
        }

        return CallNextHookEx(nint.Zero, code, wParam, lParam);
    }

    private nint MouseHookCallback(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && TryGetMouseCode(unchecked((int)wParam), lParam, out var mouseCode, out var isDown))
        {
            var token = new PressedInput(HotkeyInputKind.Mouse, mouseCode);
            if (isDown)
            {
                if (_pressedInputs.Add(token))
                {
                    TriggerIfMatched(new HotkeyBinding { Kind = HotkeyInputKind.Mouse, Code = mouseCode });
                }
            }
            else
            {
                _pressedInputs.Remove(token);
            }
        }

        return CallNextHookEx(nint.Zero, code, wParam, lParam);
    }

    private void TriggerIfMatched(HotkeyBinding binding)
    {
        try
        {
            var settings = _settingsAccessor();
            foreach (var hotkey in SoundControlAutomation.EnumerateHotkeys(settings))
            {
                if (SoundControlAutomation.BindingsEqual(hotkey.Binding, binding))
                {
                    var action = hotkey.Action;
                    ThreadPool.QueueUserWorkItem(_ => SafeTrigger(action));
                    break;
                }
            }
        }
        catch
        {
            // Hook callbacks must stay fast and never break the input chain.
        }
    }

    private void SafeTrigger(SoundHotkeyAction action)
    {
        try
        {
            _onTriggered(action);
        }
        catch
        {
        }
    }

    private static bool TryGetMouseCode(int message, nint lParam, out int mouseCode, out bool isDown)
    {
        mouseCode = 0;
        isDown = false;

        switch (message)
        {
            case WmLButtonDown:
                mouseCode = MouseLeftCode;
                isDown = true;
                return true;
            case WmLButtonUp:
                mouseCode = MouseLeftCode;
                return true;
            case WmRButtonDown:
                mouseCode = MouseRightCode;
                isDown = true;
                return true;
            case WmRButtonUp:
                mouseCode = MouseRightCode;
                return true;
            case WmMButtonDown:
                mouseCode = MouseMiddleCode;
                isDown = true;
                return true;
            case WmMButtonUp:
                mouseCode = MouseMiddleCode;
                return true;
            case WmXButtonDown:
            case WmXButtonUp:
                var mouseInfo = Marshal.PtrToStructure<MsLlHookStruct>(lParam);
                mouseCode = HiWord(mouseInfo.MouseData) == XButton1 ? MouseX1Code : MouseX2Code;
                isDown = message == WmXButtonDown;
                return true;
            default:
                return false;
        }
    }

    private static int HiWord(uint value)
    {
        return (int)((value >> 16) & 0xFFFF);
    }

    private delegate nint LowLevelProc(int code, nint wParam, nint lParam);

    private readonly record struct PressedInput(HotkeyInputKind Kind, int Code);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandle(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VirtualKeyCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsLlHookStruct
    {
        public Point Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }
}

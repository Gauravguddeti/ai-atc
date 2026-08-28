using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;

namespace MsfsAiAtc.Audio;

/// <summary>
/// Global hotkey hook using Win32 RegisterHotKey.
/// Works even when MSFS has focus, which is a system-level foreground window.
///
/// Debouncing is handled in VoicePipeline.OnPttKeyDown — this class
/// fires raw key-down/key-up events and the state machine ignores repeats.
/// </summary>
public class GlobalHotkeyHook : IDisposable
{
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID_DOWN = 9001;
    private const int HOTKEY_ID_UP = 9002; // We use low-level hook for key-up

    private readonly ILogger<GlobalHotkeyHook> _logger;
    private readonly Key _pttKey;
    private IntPtr _hwnd;
#pragma warning disable CS0169
    private System.Windows.Interop.HwndSource? _hwndSource;
#pragma warning restore CS0169

    private bool _isKeyCurrentlyDown;
    private bool _disposed;

    // Low-level keyboard hook handle
    private IntPtr _hookHandle = IntPtr.Zero;

    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc callback, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string? lpModuleName);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    private LowLevelKeyboardProc? _hookCallback; // keep reference to prevent GC

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    public event Action? PttKeyDown;
    public event Action? PttKeyUp;

    public GlobalHotkeyHook(ILogger<GlobalHotkeyHook> logger, Key pttKey)
    {
        _logger = logger;
        _pttKey = pttKey;
    }

    public void Initialize(IntPtr hwnd)
    {
        _hwnd = hwnd;
        InstallLowLevelHook();
        _logger.LogInformation("Global hotkey hook installed for key: {Key}", _pttKey);
    }

    private void InstallLowLevelHook()
    {
        _hookCallback = HookCallback;
        var hMod = GetModuleHandle(null);
        _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _hookCallback, hMod, 0);
        if (_hookHandle == IntPtr.Zero)
            _logger.LogWarning("Failed to install low-level keyboard hook — PTT may not work");
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int vkCode = Marshal.ReadInt32(lParam);
            var key = KeyInterop.KeyFromVirtualKey(vkCode);

            if (key == _pttKey)
            {
                int msg = wParam.ToInt32();
                if ((msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN) && !_isKeyCurrentlyDown)
                {
                    _isKeyCurrentlyDown = true;
                    PttKeyDown?.Invoke();
                }
                else if ((msg == WM_KEYUP || msg == WM_SYSKEYUP) && _isKeyCurrentlyDown)
                {
                    _isKeyCurrentlyDown = false;
                    PttKeyUp?.Invoke();
                }
            }
        }
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }
}

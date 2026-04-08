using System.Runtime.InteropServices;

namespace ClickyWindows.Services;

/// <summary>
/// Detects system-wide push-to-talk hold/release transitions for Ctrl+Alt.
///
/// A dedicated low-level keyboard hook thread is used so the app still receives
/// keyboard state changes while it is fully in the background.
/// </summary>
public sealed class GlobalHotkeyMonitor : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;

    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const uint WM_QUIT = 0x0012;

    private const int VK_CONTROL = 0x11;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_MENU = 0x12;
    private const int VK_LMENU = 0xA4;
    private const int VK_RMENU = 0xA5;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr WindowHandle;
        public uint MessageId;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public Point CursorPoint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelKeyboardInputEvent
    {
        public uint VirtualKeyCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    private delegate IntPtr LowLevelKeyboardCallback(
        int hookCode,
        IntPtr messageIdentifier,
        IntPtr keyboardDataPointer
    );

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int hookType,
        LowLevelKeyboardCallback callback,
        IntPtr moduleHandle,
        uint targetThreadId
    );

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hookHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hookHandle,
        int hookCode,
        IntPtr messageIdentifier,
        IntPtr keyboardDataPointer
    );

    [DllImport("user32.dll")]
    private static extern int GetMessage(
        out NativeMessage message,
        IntPtr windowHandle,
        uint minimumMessageFilter,
        uint maximumMessageFilter
    );

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage([In] ref NativeMessage message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage([In] ref NativeMessage message);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(
        uint threadId,
        uint messageId,
        nuint wParam,
        nint lParam
    );

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    public event Action? HotkeyPressed;
    public event Action? HotkeyReleased;

    private readonly object _modifierStateLock = new();
    private readonly AutoResetEvent _hookReadyEvent = new(false);
    private readonly Thread _keyboardHookThread;

    private LowLevelKeyboardCallback? _hookCallback;
    private IntPtr _hookHandle = IntPtr.Zero;
    private uint _hookThreadId;

    private bool _isLeftControlKeyDown;
    private bool _isRightControlKeyDown;
    private bool _isLeftAltKeyDown;
    private bool _isRightAltKeyDown;
    private bool _isHotkeyCurrentlyHeld;

    private bool _disposed;

    public GlobalHotkeyMonitor()
    {
        _keyboardHookThread = new Thread(RunKeyboardHookLoop)
        {
            IsBackground = true,
            Name = "Clicky Global Hotkey Hook Thread"
        };
        _keyboardHookThread.Start();

        if (!_hookReadyEvent.WaitOne(TimeSpan.FromSeconds(2)))
        {
            Console.WriteLine(
                "Clicky: Timed out while starting global keyboard hook thread."
            );
        }
    }

    private void RunKeyboardHookLoop()
    {
        try
        {
            _hookThreadId = GetCurrentThreadId();
            _hookCallback = OnLowLevelKeyboardEvent;
            _hookHandle = SetWindowsHookEx(
                WH_KEYBOARD_LL,
                _hookCallback,
                IntPtr.Zero,
                0
            );

            if (_hookHandle == IntPtr.Zero)
            {
                int win32ErrorCode = Marshal.GetLastWin32Error();
                Console.WriteLine(
                    $"Clicky: Failed to install WH_KEYBOARD_LL hook. Win32 error code: {win32ErrorCode}"
                );
                return;
            }

            Console.WriteLine(
                "Clicky: Global keyboard hook active for Ctrl+Alt push-to-talk."
            );

            _hookReadyEvent.Set();

            while (true)
            {
                int getMessageResult = GetMessage(
                    out NativeMessage message,
                    IntPtr.Zero,
                    0,
                    0
                );

                if (getMessageResult <= 0)
                {
                    break;
                }

                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        finally
        {
            _hookReadyEvent.Set();
            UnhookKeyboardHookIfNeeded();
        }
    }

    private IntPtr OnLowLevelKeyboardEvent(
        int hookCode,
        IntPtr messageIdentifier,
        IntPtr keyboardDataPointer
    )
    {
        if (hookCode < 0)
        {
            return CallNextHookEx(_hookHandle, hookCode, messageIdentifier, keyboardDataPointer);
        }

        int messageId = messageIdentifier.ToInt32();
        bool isKeyDownMessage = messageId == WM_KEYDOWN || messageId == WM_SYSKEYDOWN;
        bool isKeyUpMessage = messageId == WM_KEYUP || messageId == WM_SYSKEYUP;

        if (!isKeyDownMessage && !isKeyUpMessage)
        {
            return CallNextHookEx(_hookHandle, hookCode, messageIdentifier, keyboardDataPointer);
        }

        bool isKeyDown = isKeyDownMessage;
        var keyboardInputEvent = Marshal.PtrToStructure<LowLevelKeyboardInputEvent>(keyboardDataPointer);
        UpdateModifierStateAndEmitHotkeyTransitions(
            (int)keyboardInputEvent.VirtualKeyCode,
            isKeyDown
        );

        return CallNextHookEx(_hookHandle, hookCode, messageIdentifier, keyboardDataPointer);
    }

    private void UpdateModifierStateAndEmitHotkeyTransitions(
        int virtualKeyCode,
        bool isKeyDown
    )
    {
        bool shouldEmitPressedEvent = false;
        bool shouldEmitReleasedEvent = false;

        lock (_modifierStateLock)
        {
            switch (virtualKeyCode)
            {
                case VK_LCONTROL:
                    _isLeftControlKeyDown = isKeyDown;
                    break;
                case VK_RCONTROL:
                    _isRightControlKeyDown = isKeyDown;
                    break;
                case VK_CONTROL:
                    _isLeftControlKeyDown = isKeyDown;
                    _isRightControlKeyDown = isKeyDown;
                    break;
                case VK_LMENU:
                    _isLeftAltKeyDown = isKeyDown;
                    break;
                case VK_RMENU:
                    _isRightAltKeyDown = isKeyDown;
                    break;
                case VK_MENU:
                    _isLeftAltKeyDown = isKeyDown;
                    _isRightAltKeyDown = isKeyDown;
                    break;
                default:
                    return;
            }

            bool isAnyControlKeyDown = _isLeftControlKeyDown || _isRightControlKeyDown;
            bool isAnyAltKeyDown = _isLeftAltKeyDown || _isRightAltKeyDown;
            bool isHotkeyHeldNow = isAnyControlKeyDown && isAnyAltKeyDown;

            if (isHotkeyHeldNow && !_isHotkeyCurrentlyHeld)
            {
                _isHotkeyCurrentlyHeld = true;
                shouldEmitPressedEvent = true;
            }
            else if (!isHotkeyHeldNow && _isHotkeyCurrentlyHeld)
            {
                _isHotkeyCurrentlyHeld = false;
                shouldEmitReleasedEvent = true;
            }
        }

        if (shouldEmitPressedEvent)
        {
            Console.WriteLine("Clicky: Push-to-talk pressed (Ctrl+Alt).");
            HotkeyPressed?.Invoke();
        }

        if (shouldEmitReleasedEvent)
        {
            Console.WriteLine("Clicky: Push-to-talk released (Ctrl+Alt).");
            HotkeyReleased?.Invoke();
        }
    }

    private void UnhookKeyboardHookIfNeeded()
    {
        if (_hookHandle == IntPtr.Zero)
        {
            return;
        }

        if (!UnhookWindowsHookEx(_hookHandle))
        {
            int win32ErrorCode = Marshal.GetLastWin32Error();
            Console.WriteLine(
                $"Clicky: Failed to unhook WH_KEYBOARD_LL. Win32 error code: {win32ErrorCode}"
            );
        }

        _hookHandle = IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        lock (_modifierStateLock)
        {
            _isLeftControlKeyDown = false;
            _isRightControlKeyDown = false;
            _isLeftAltKeyDown = false;
            _isRightAltKeyDown = false;
            _isHotkeyCurrentlyHeld = false;
        }

        if (_hookThreadId != 0)
        {
            PostThreadMessage(_hookThreadId, WM_QUIT, 0, 0);
        }

        if (_keyboardHookThread.IsAlive)
        {
            _keyboardHookThread.Join(TimeSpan.FromSeconds(1));
        }

        UnhookKeyboardHookIfNeeded();
        _hookReadyEvent.Dispose();
    }
}

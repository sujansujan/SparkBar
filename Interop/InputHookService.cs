using System;
using System.Threading;

namespace SparkBar;

/// <summary>
/// Dedicated low-level mouse &amp; keyboard hook thread so OS cursor/input is never delayed.
/// </summary>
internal sealed unsafe class InputHookService : IDisposable
{
    private Thread _hookThread;
    private uint _hookThreadId;
    private IntPtr _mouseHook;
    private IntPtr _kbdHook;
    private Native.LowLevelProc _mouseProc;
    private Native.LowLevelProc _kbdProc;

    public Func<string, bool> IsHintKeyBound { get; set; }
    public Func<bool> IsAnyPopupOrQrOrTypingActive { get; set; }
    public Func<bool> IsSecondaryPopupActive { get; set; }

    public event Action<int, int> MouseLeftDown;
    public event Action<int, int> MouseLeftUp;
    public event Action<int, int> MouseRightUp;
    public event Action MouseWheelOrMiddle;
    public event Action<string> HintKeyPressed;
    public event Action EscapePressed;
    public event Action BackspacePressed;
    public event Action AnyOtherKeyPressed;
    public event Action ShiftSelectionKeyReleased;
    public event Action ManualTriggerHotkey;      // Ctrl+Alt+P
    public event Action ToggleExcludeAppHotkey;   // Ctrl+Alt+X
    public event Action ClipboardHistoryHotkey;   // Ctrl+Alt+V

    public void Start()
    {
        if (_hookThread != null) return;
        _hookThread = new Thread(HookThreadMain)
        {
            IsBackground = true,
            Name = "SparkBarInputHook",
            Priority = ThreadPriority.Highest
        };
        _hookThread.Start();
    }

    private void HookThreadMain()
    {
        _hookThreadId = Native.GetCurrentThreadId();
        _mouseProc = MouseHookCallback;
        _kbdProc = KeyboardHookCallback;

        IntPtr hMod = Native.GetModuleHandle(null);
        _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, hMod, 0);
        _kbdHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _kbdProc, hMod, 0);

        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == Native.WM_QUIT) break;
        }

        if (_mouseHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
        if (_kbdHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_kbdHook); _kbdHook = IntPtr.Zero; }
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            var ms = *(Native.MSLLHOOKSTRUCT*)lParam;
            switch (msg)
            {
                case Native.WM_LBUTTONDOWN:
                    MouseLeftDown?.Invoke(ms.pt.X, ms.pt.Y);
                    break;
                case Native.WM_LBUTTONUP:
                    MouseLeftUp?.Invoke(ms.pt.X, ms.pt.Y);
                    break;
                case Native.WM_RBUTTONUP:
                    MouseRightUp?.Invoke(ms.pt.X, ms.pt.Y);
                    break;
                case Native.WM_MBUTTONDOWN:
                case Native.WM_MOUSEWHEEL:
                case Native.WM_MOUSEHWHEEL:
                    MouseWheelOrMiddle?.Invoke();
                    break;
            }
        }
        return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            var kb = *(Native.KBDLLHOOKSTRUCT*)lParam;

            if ((kb.flags & Native.LLKHF_INJECTED) != 0)
                return Native.CallNextHookEx(_kbdHook, nCode, wParam, lParam);

            int vk = (int)kb.vkCode;
            bool isDown = (msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN);
            bool isUp   = (msg == Native.WM_KEYUP   || msg == Native.WM_SYSKEYUP);

            bool ctrl  = Native.IsKeyDown(Native.VK_CONTROL) || Native.IsKeyDown(Native.VK_LCONTROL) || Native.IsKeyDown(Native.VK_RCONTROL);
            bool alt   = Native.IsKeyDown(Native.VK_MENU)    || Native.IsKeyDown(Native.VK_LMENU)    || Native.IsKeyDown(Native.VK_RMENU);
            bool shift = Native.IsKeyDown(Native.VK_SHIFT)   || Native.IsKeyDown(Native.VK_LSHIFT)   || Native.IsKeyDown(Native.VK_RSHIFT);
            bool win   = Native.IsKeyDown(Native.VK_LWIN)    || Native.IsKeyDown(Native.VK_RWIN);

            if (isDown)
            {
                if (ctrl && alt && !shift && !win)
                {
                    if (vk == 'P')
                    {
                        ManualTriggerHotkey?.Invoke();
                        return new IntPtr(1);
                    }
                    if (vk == 'X')
                    {
                        ToggleExcludeAppHotkey?.Invoke();
                        return new IntPtr(1);
                    }
                    if (vk == 'V')
                    {
                        ClipboardHistoryHotkey?.Invoke();
                        return new IntPtr(1);
                    }
                }

                if (vk == Native.VK_ESCAPE && (IsAnyPopupOrQrOrTypingActive?.Invoke() ?? false))
                {
                    EscapePressed?.Invoke();
                    return new IntPtr(1);
                }

                if (vk == Native.VK_BACK && !ctrl && !alt && !shift && !win && (IsSecondaryPopupActive?.Invoke() ?? false))
                {
                    BackspacePressed?.Invoke();
                    return new IntPtr(1);
                }

                if (vk is Native.VK_SHIFT or Native.VK_CONTROL or Native.VK_MENU
                    or Native.VK_LSHIFT or Native.VK_RSHIFT
                    or Native.VK_LCONTROL or Native.VK_RCONTROL
                    or Native.VK_LMENU or Native.VK_RMENU
                    or Native.VK_LWIN or Native.VK_RWIN)
                {
                    return Native.CallNextHookEx(_kbdHook, nCode, wParam, lParam);
                }

                if (!ctrl && !alt && !shift && !win &&
                    ((vk >= '1' && vk <= '9') || (vk >= 'A' && vk <= 'Z')))
                {
                    string key = ((char)vk).ToString();
                    if (IsHintKeyBound != null && IsHintKeyBound(key))
                    {
                        HintKeyPressed?.Invoke(key);
                        return new IntPtr(1);
                    }
                }

                AnyOtherKeyPressed?.Invoke();
            }
            else if (isUp)
            {
                if (shift && vk is Native.VK_LEFT or Native.VK_RIGHT or Native.VK_UP or Native.VK_DOWN or Native.VK_HOME or Native.VK_END)
                {
                    ShiftSelectionKeyReleased?.Invoke();
                }
            }
        }
        return Native.CallNextHookEx(_kbdHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hookThreadId != 0)
        {
            Native.PostThreadMessage(_hookThreadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _hookThreadId = 0;
        }
    }
}

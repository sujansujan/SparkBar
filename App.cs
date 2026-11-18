using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace SparkBar;

public sealed class App : Application
{
    private static Mutex _singleInstanceMutex;

    private ConfigService _cfg;
    private InputHookService _hooks;
    private TrayIconManager _tray;
    private DispatcherTimer _selectionTimer;
    private DispatcherTimer _kbdDebounceTimer;
    private IntPtr _winEventHook;
    private Native.WinEventProc _winEventProc;

    // Dedicated selection worker thread for zero-latency UIA & clipboard probing
    private Thread _selWorkerThread;
    private readonly AutoResetEvent _selSignal = new(false);
    private volatile bool _shuttingDown;

    // Pre-warmed persistent popup toolbar window
    private PopupToolbarWindow _popupWindow;
    private OptionMenuWindow _optionMenuWindow;
    private ResultCardWindow _resultCardWindow;
    private ToastWindow _toastWindow;
    private QrWindow _qrWindow;
    private UrlBuilderWindow _urlBuilderWindow;
    private SettingsWindow _settingsWindow;

    private bool _enabled = true;
    private volatile bool _selBusy;
    private volatile bool _typingActive;
    private volatile bool _verifyProximityOnNextCheck;

    private string _selText = "";
    private bool _popupEditable;
    private bool _popupClipboardMode;
    private bool _lastReturnClipboardMode;
    private int _popX, _popY;
    private Native.RECT _lastPopupRect;
    private IntPtr _lastActiveWin;

    private int _downX, _downY;
    private bool _downOK;
    private bool _ignoreClick;
    private long _lastUpTick;
    private int _lastUpX, _lastUpY;
    private int _clickCount;

    [STAThread]
    public static void Main(string[] args)
    {
        _singleInstanceMutex = new Mutex(true, @"Global\SparkBar_SingleInstance_Mutex", out bool createdNew);
        if (!createdNew && !args.Contains("--allow-multi"))
        {
            // Ask the existing running instance to gracefully shut down so the newly launched binary can take over
            uint msg = Native.RegisterWindowMessage("SparkBar_GracefulShutdown");
            if (msg != 0)
            {
                Native.PostMessage(Native.HWND_BROADCAST, msg, new IntPtr(Environment.ProcessId), IntPtr.Zero);
                Thread.Sleep(350);
            }
        }

        var app = new App
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };
        app.InitializeApp(args);
        app.Run();
    }

    private void InitializeApp(string[] args)
    {
        _cfg = new ConfigService();

        DispatcherUnhandledException += (_, e) =>
        {
            _cfg.LogError(e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                _cfg.LogError(ex);
        };

        _cfg.UpdatePalette();

        // Pre-warm persistent flyout windows so showing any toolbar, menu, or toast takes < 2ms
        _popupWindow = new PopupToolbarWindow(_cfg, ReorderPopupItem);
        _popupWindow.PreWarm();
        _optionMenuWindow = new OptionMenuWindow(_cfg);
        _optionMenuWindow.PreWarm();
        _resultCardWindow = new ResultCardWindow(
            _cfg,
            onCopyText: txt => { UiaHelper.SetClipboardUnicodeText(txt); _cfg.RecordClipboardItem(txt); },
            onReplaceText: (win, txt) => ApplyTransform(win, _selText, editable: true, _ => txt),
            onOpenWebUrl: OpenUrl,
            onToast: msg => ShowToast(msg),
            onBackToToolbar: ReturnToMainPopup);
        _resultCardWindow.PreWarm();
        _toastWindow = new ToastWindow(_cfg);
        _toastWindow.PreWarm();

        // Start dedicated selection worker thread (owns its own thread-local IUIAutomation COM instance)
        _selWorkerThread = new Thread(SelectionWorkerLoop)
        {
            IsBackground = true,
            Name = "SparkBarSelectionWorker",
            Priority = ThreadPriority.AboveNormal
        };
        _selWorkerThread.Start();

        _selectionTimer = new DispatcherTimer(DispatcherPriority.Render);
        _selectionTimer.Tick += (_, _) =>
        {
            _selectionTimer.Stop();
            RequestSelectionCheck();
        };

        _kbdDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _kbdDebounceTimer.Tick += (_, _) =>
        {
            _kbdDebounceTimer.Stop();
            if (!_enabled || !_cfg.KeyboardTriggerEnabled || IsPopupOpen) return;
            if (Native.IsKeyDown(Native.VK_SHIFT) || Native.IsKeyDown(Native.VK_LSHIFT) || Native.IsKeyDown(Native.VK_RSHIFT))
                return;
            if (IsExcludedActiveWindow()) return;
            UiaHelper.ResolveKeyboardAnchor(out _popX, out _popY);
            _verifyProximityOnNextCheck = false;
            RequestSelectionCheck();
        };

        _winEventProc = OnForegroundWindowChanged;
        _winEventHook = Native.SetWinEventHook(
            Native.EVENT_SYSTEM_FOREGROUND,
            Native.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _winEventProc,
            0, 0,
            Native.WINEVENT_OUTOFCONTEXT);

        _tray = new TrayIconManager(
            _cfg,
            getEnabled: () => _enabled,
            onToggleEnabled: ToggleEnabled,
            onSelectDefaultEngine: name => SetDefaultSearchEngineLive(name, notify: true),
            onToggleExcludeActiveApp: ToggleExcludeActiveApp,
            onOpenSettings: ShowSettingsWindow,
            onOpenClipboardHistory: ShowClipboardHistoryMenu,
            onOpenErrorLog: OpenErrorLog,
            onReload: ReloadAppConfiguration,
            onExit: ExitApplication,
            onSystemThemeChanged: () =>
            {
                if (_cfg.ThemeMode == "auto")
                    _cfg.UpdatePalette();
            });

        _hooks = new InputHookService
        {
            IsHintKeyBound = IsHintKeyBound,
            IsAnyPopupOrQrOrTypingActive = () => IsPopupOpen || IsOptionMenuOpen || IsResultCardOpen || _qrWindow != null || (_tray?.IsFlyoutOpen ?? false) || _typingActive,
            IsSecondaryPopupActive = () => IsOptionMenuOpen || IsResultCardOpen || _qrWindow != null
        };

        _hooks.MouseLeftDown += (x, y) => Dispatcher.BeginInvoke(DispatcherPriority.Send, () => HandleMouseDown(x, y));
        _hooks.MouseLeftUp += (x, y) => Dispatcher.BeginInvoke(DispatcherPriority.Send, () => HandleMouseUp(x, y));
        _hooks.MouseRightUp += (x, y) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () => HandleMouseRightUp(x, y));
        _hooks.MouseWheelOrMiddle += () => Dispatcher.BeginInvoke(DispatcherPriority.Input, HandleMouseWheelOrMiddle);
        _hooks.HintKeyPressed += key => Dispatcher.BeginInvoke(DispatcherPriority.Send, () => HandleHintKey(key));
        _hooks.EscapePressed += () => Dispatcher.BeginInvoke(DispatcherPriority.Send, HandleEscape);
        _hooks.BackspacePressed += () => Dispatcher.BeginInvoke(DispatcherPriority.Send, ReturnToMainPopup);
        _hooks.AnyOtherKeyPressed += () => Dispatcher.BeginInvoke(DispatcherPriority.Input, () => DismissAllMenus());
        _hooks.ShiftSelectionKeyReleased += () =>
        {
            if (_enabled && _cfg.KeyboardTriggerEnabled)
                Dispatcher.BeginInvoke(() => { _kbdDebounceTimer.Stop(); _kbdDebounceTimer.Start(); });
        };
        _hooks.ManualTriggerHotkey += () => Dispatcher.BeginInvoke(DispatcherPriority.Send, TriggerManualPopup);
        _hooks.ToggleExcludeAppHotkey += () => Dispatcher.BeginInvoke(ToggleExcludeActiveApp);
        _hooks.ClipboardHistoryHotkey += () => Dispatcher.BeginInvoke(DispatcherPriority.Send, ShowClipboardHistoryMenu);

        _hooks.Start();

        if (args.Contains("--settings"))
            Dispatcher.BeginInvoke(ShowSettingsWindow);

        // Trim unused startup metadata pages once after pre-warming completes
        var startupTrim = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        startupTrim.Tick += (_, _) =>
        {
            startupTrim.Stop();
            // Pre-JIT BuildActions & PopulateActions once while hidden so the very first real popup is 0ms!
            var warmActs = BuildActions("125 km", editable: true, clipboardMode: false);
            _popupWindow.PopulateActions(warmActs);
            Native.TrimWorkingSet();
        };
        startupTrim.Start();
    }

    private bool IsPopupOpen => _popupWindow != null && _popupWindow.IsPopupOpen;
    private bool IsOptionMenuOpen => _optionMenuWindow != null && _optionMenuWindow.IsMenuOpen;
    private bool IsResultCardOpen => _resultCardWindow != null && _resultCardWindow.IsCardOpen;

    private void ApplyFluentAppTheme()
    {
        var pal = _cfg.UpdatePalette();
        try
        {
            ThemeMode = pal.IsLight ? System.Windows.ThemeMode.Light : System.Windows.ThemeMode.Dark;
        }
        catch { }
    }

    private void OnForegroundWindowChanged(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
    {
        if (!IsPopupOpen && !IsOptionMenuOpen && !IsResultCardOpen && _qrWindow == null) return;
        if (hwnd == IntPtr.Zero || Native.IsOwnWindow(hwnd)) return;
        if (_lastActiveWin != IntPtr.Zero && hwnd != _lastActiveWin)
            Dispatcher.BeginInvoke(() => DismissAllMenus());
    }

    // ---------------- Mouse & Keyboard Event Handlers ----------------
    private void HandleMouseDown(int x, int y)
    {
        _ignoreClick = false;
        _downOK = false;
        _downX = x;
        _downY = y;

        IntPtr rootWin = Native.GetAncestor(Native.WindowFromPoint(new Native.POINT(x, y)), Native.GA_ROOT);

        if (_tray != null && _tray.IsFlyoutOpen)
        {
            if (rootWin == _tray.FlyoutHwnd)
            {
                _ignoreClick = true;
                return;
            }
            _tray.CloseFlyout();
        }

        if (IsResultCardOpen)
        {
            if (rootWin == _resultCardWindow.Hwnd)
            {
                _ignoreClick = true;
                return;
            }
            HideResultCard();
        }

        if (_qrWindow != null)
        {
            if (rootWin == _qrWindow.Hwnd)
                _ignoreClick = true;
            else
                HideQr();
            return;
        }

        if (IsPopupOpen)
        {
            if (rootWin == _popupWindow.Hwnd)
            {
                _ignoreClick = true;
                return;
            }
            HidePopup();
        }

        if (IsOptionMenuOpen)
        {
            if (rootWin == _optionMenuWindow.Hwnd)
            {
                _ignoreClick = true;
                return;
            }
            HideOptionMenu();
        }

        if (_enabled)
            _downOK = IsClientHit(x, y, rootWin);
    }

    private void HandleMouseUp(int x, int y)
    {
        if (_ignoreClick)
        {
            _ignoreClick = false;
            return;
        }

        bool ctrl = Native.IsKeyDown(Native.VK_CONTROL) || Native.IsKeyDown(Native.VK_LCONTROL) || Native.IsKeyDown(Native.VK_RCONTROL);
        bool alt  = Native.IsKeyDown(Native.VK_MENU)    || Native.IsKeyDown(Native.VK_LMENU)    || Native.IsKeyDown(Native.VK_RMENU);
        bool lwin = Native.IsKeyDown(Native.VK_LWIN)    || Native.IsKeyDown(Native.VK_RWIN);

        if (!_enabled || !_downOK || ctrl || alt || lwin)
            return;

        long now = Environment.TickCount64;
        bool dragged = (Math.Abs(x - _downX) + Math.Abs(y - _downY)) >= _cfg.MinDragPx;
        uint dblTime = Native.GetDoubleClickTime();
        _clickCount = (Math.Abs(x - _lastUpX) + Math.Abs(y - _lastUpY) <= 6 && (now - _lastUpTick <= dblTime))
            ? _clickCount + 1
            : 1;
        _lastUpTick = now;
        _lastUpX = x;
        _lastUpY = y;

        // Ultra-responsive trigger timings: 18ms for drag / triple-click, 90ms for double-click
        if (dragged || _clickCount >= 3)
        {
            _clickCount = 0;
            _popX = x;
            _popY = y;
            _verifyProximityOnNextCheck = true;
            ScheduleSelectionCheck(18);
        }
        else if (_clickCount == 2)
        {
            _popX = x;
            _popY = y;
            _verifyProximityOnNextCheck = true;
            ScheduleSelectionCheck(90);
        }
    }

    private void HandleMouseRightUp(int x, int y)
    {
        if (!IsPopupOpen && !IsOptionMenuOpen && !IsResultCardOpen) return;
        IntPtr rootWin = Native.GetAncestor(Native.WindowFromPoint(new Native.POINT(x, y)), Native.GA_ROOT);
        if (IsPopupOpen && rootWin == _popupWindow.Hwnd)
            return;
        if (IsResultCardOpen && rootWin == _resultCardWindow.Hwnd)
            return;
        DismissAllMenus();
    }

    private void HandleMouseWheelOrMiddle()
    {
        if (!IsPopupOpen && !IsOptionMenuOpen && !IsResultCardOpen && _qrWindow == null)
            return;
        Native.GetCursorPos(out var cur);
        IntPtr rootWin = Native.GetAncestor(Native.WindowFromPoint(cur), Native.GA_ROOT);
        if (rootWin != IntPtr.Zero &&
            ((IsResultCardOpen && rootWin == _resultCardWindow.Hwnd) ||
             (IsOptionMenuOpen && rootWin == _optionMenuWindow.Hwnd)))
            return;
        DismissAllMenus();
    }

    private void ScheduleSelectionCheck(int delayMs)
    {
        _selectionTimer.Stop();
        _selectionTimer.Interval = TimeSpan.FromMilliseconds(delayMs);
        _selectionTimer.Start();
    }

    private static bool IsClientHit(int x, int y, IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            IntPtr lParam = new((x & 0xFFFF) | ((y & 0xFFFF) << 16));
            if (Native.SendMessageTimeout(hwnd, Native.WM_NCHITTEST, IntPtr.Zero, lParam, Native.SMTO_ABORTIFHUNG, 80, out IntPtr hit) != IntPtr.Zero)
            {
                if (hit.ToInt32() != Native.HTCLIENT)
                    return false;
            }
            if (Native.GetClientRect(hwnd, out var rc))
            {
                var pt = new Native.POINT(0, 0);
                Native.ClientToScreen(hwnd, ref pt);
                int cw = rc.Width;
                double scale = Native.ScaleAt(x, y);
                int scrollBarWidth = (int)Math.Round(18 * scale);
                if (cw > 100 && x >= pt.X + cw - scrollBarWidth)
                    return false;
            }
        }
        catch { }
        return true;
    }

    private bool IsFullscreenActive()
    {
        if (!_cfg.PauseInFullscreen) return false;
        try
        {
            IntPtr hwnd = Native.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;
            string cls = Native.ClassOf(hwnd);
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
            long style = Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE).ToInt64();
            if ((style & Native.WS_CAPTION) == Native.WS_CAPTION) return false;
            if (!Native.GetWindowRect(hwnd, out var wr)) return false;
            Native.WorkAreaAt(wr.Left + wr.Width / 2, wr.Top + wr.Height / 2, out var mon);
            return wr.Left <= mon.Left && wr.Top <= mon.Top && wr.Width >= mon.Width && wr.Height >= mon.Height;
        }
        catch { return false; }
    }

    private bool IsExcludedActiveWindow()
    {
        if (IsFullscreenActive()) return true;
        IntPtr fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || Native.IsOwnWindow(fg)) return true;
        string exe = Native.ProcessNameOf(fg);
        string cls = Native.ClassOf(fg);
        return _cfg.ExcludedExes.Contains(exe) || _cfg.ExcludedClasses.Contains(cls);
    }

    private void RequestSelectionCheck()
    {
        if (_selBusy || IsExcludedActiveWindow() || Native.IsKeyDown(Native.VK_LBUTTON))
            return;
        _selSignal.Set();
    }

    private void SelectionWorkerLoop()
    {
        Native.CoInitializeEx(IntPtr.Zero, 0 /*COINIT_MULTITHREADED*/);
        // Pre-warm thread-local IUIAutomation on worker thread startup
        _ = UiaHelper.IsEditableFocus(true);

        while (!_shuttingDown)
        {
            _selSignal.WaitOne();
            if (_shuttingDown) break;
            if (_selBusy || Native.IsKeyDown(Native.VK_LBUTTON)) continue;

            _selBusy = true;
            try
            {
                int? vx = _verifyProximityOnNextCheck ? _popX : null;
                int? vy = _verifyProximityOnNextCheck ? _popY : null;
                string text = UiaHelper.GetSelectionText(_cfg, vx, vy);
                if (!string.IsNullOrWhiteSpace(text) && text.Length <= _cfg.MaxChars && !Native.IsKeyDown(Native.VK_LBUTTON))
                {
                    bool editable = UiaHelper.IsEditableFocus(_cfg.ShowEditWhenUnknown);
                    Dispatcher.BeginInvoke(DispatcherPriority.Send, () =>
                    {
                        if (!Native.IsKeyDown(Native.VK_LBUTTON))
                            ShowPopup(text, editable, clipboardMode: false);
                    });
                }
            }
            catch { }
            finally { _selBusy = false; }
        }
    }

    private async void TriggerManualPopup()
    {
        if (!_enabled) return;
        UiaHelper.ResolveKeyboardAnchor(out _popX, out _popY);

        var cfg = _cfg;
        var (sel, editable) = await Task.Run(() =>
        {
            string t = UiaHelper.GetSelectionText(cfg);
            bool ed = UiaHelper.IsEditableFocus(cfg.ShowEditWhenUnknown);
            return (t, ed);
        });

        if (!string.IsNullOrWhiteSpace(sel) && sel.Length <= _cfg.MaxChars)
        {
            ShowPopup(sel, editable, clipboardMode: false);
            return;
        }

        string clip = UiaHelper.GetClipboardUnicodeText();
        if (!string.IsNullOrWhiteSpace(clip) && clip.Length <= _cfg.MaxChars)
            ShowPopup(clip, editable, clipboardMode: true);
    }

    private bool IsHintKeyBound(string key)
    {
        if (IsResultCardOpen) return _resultCardWindow.HasHintKey(key);
        if (_qrWindow != null) return _qrWindow.HasHintKey(key);
        if (IsOptionMenuOpen) return _optionMenuWindow.HasHintKey(key);
        if (IsPopupOpen) return _popupWindow.HasHintKey(key);
        return false;
    }

    private void HandleHintKey(string key)
    {
        if (IsResultCardOpen && _resultCardWindow.HasHintKey(key))
            _resultCardWindow.InvokeHintKey(key);
        else if (_qrWindow != null && _qrWindow.HasHintKey(key))
            _qrWindow.InvokeHintKey(key);
        else if (IsOptionMenuOpen && _optionMenuWindow.HasHintKey(key))
            _optionMenuWindow.InvokeHintKey(key);
        else if (IsPopupOpen && _popupWindow.HasHintKey(key))
            _popupWindow.InvokeHintKey(key);
    }

    private void HandleEscape()
    {
        if (_typingActive)
        {
            _typingActive = false;
            return;
        }
        if (_qrWindow != null)
        {
            HideQr();
            return;
        }
        if (IsResultCardOpen)
        {
            HideResultCard();
            return;
        }
        _tray?.CloseFlyout();
        DismissAllMenus();
    }

    // ---------------- Popup & Option Menu Management ----------------
    public void ShowPopup(string text, bool editable, bool clipboardMode, bool instant = false)
    {
        if (_cfg.ThemeMode == "auto")
            _cfg.UpdatePalette();

        _lastActiveWin = Native.GetForegroundWindow();
        _selText = text;
        _popupEditable = editable;
        _popupClipboardMode = clipboardMode;
        _lastReturnClipboardMode = clipboardMode;

        var actions = BuildActions(text, editable, clipboardMode);
        if (actions.Count == 0)
        {
            HidePopup();
            return;
        }

        _popupWindow.ShowActionsAt(actions, _popX, _popY, instant);
        _lastPopupRect = _popupWindow.LastScreenRect;
    }

    public void HidePopup(bool instant = false)
    {
        _popupWindow?.HideToolbar();
        _popupClipboardMode = false;
    }

    public void ShowThemedMenu(IReadOnlyList<OptionMenuItem> items, string headerTitle, bool allowBack = true, bool useLastPopupRect = true)
    {
        _optionMenuWindow?.ShowItemsAt(
            items,
            headerTitle,
            _popX,
            _popY,
            useLastPopupRect ? _lastPopupRect : default,
            (allowBack && !string.IsNullOrWhiteSpace(_selText)) ? ReturnToMainPopup : null);
    }

    private void ReturnToMainPopup()
    {
        HideOptionMenu();
        HideResultCard();
        HideQr();
        if (!string.IsNullOrWhiteSpace(_selText))
            ShowPopup(_selText, _popupEditable, _lastReturnClipboardMode, instant: true);
    }

    public void HideOptionMenu(bool instant = false)
    {
        _optionMenuWindow?.HideMenu();
    }

    public void HideResultCard()
    {
        _resultCardWindow?.HideCard();
    }

    public void DismissAllMenus(bool instant = false)
    {
        HideOptionMenu(instant);
        HidePopup(instant);
        HideResultCard();
        HideQr();
    }

    public void ShowToast(string message, string kind = "ok", int holdMs = 1100)
    {
        _toastWindow?.ShowMessageNearCursor(message, kind, holdMs);
    }

    public void ShowNotice(string message) => ShowToast(message, "warn", 2200);

    private void HideQr()
    {
        if (_qrWindow != null)
        {
            var w = _qrWindow;
            _qrWindow = null;
            w.Dismiss();
        }
    }

    // ---------------- Contextual Action Catalog Builder ----------------
    private List<PopupActionItem> BuildActions(string text, bool editable, bool clipboardMode)
    {
        string t = (text ?? "").Trim();
        var acts = new List<PopupActionItem>();

        bool isUrl   = Regex.IsMatch(t, @"^(https?://|www\.)\S+$", RegexOptions.IgnoreCase);
        bool isEmail = !isUrl && Regex.IsMatch(t, @"^[\w.+-]+@[\w-]+(\.[\w-]+)+$", RegexOptions.IgnoreCase);
        bool isPath  = !isUrl && !isEmail && SmartParser.IsLocalOrAllowedPath(t, _cfg.AllowUncPaths);

        var defaultE = _cfg.GetDefaultSearchEngine();
        if (defaultE != null)
        {
            acts.Add(new PopupActionItem
            {
                Tip = "Search " + defaultE.Name,
                ShortLabel = defaultE.Name,
                Glyph = !string.IsNullOrEmpty(defaultE.Glyph) ? defaultE.Glyph : "\uE721",
                IsIconGlyph = true,
                Image = _cfg.GetFaviconBitmap(defaultE),
                Group = 0,
                RelevancePriority = (isUrl || isEmail || isPath) ? 2 : 1,
                OnClick = () => RunSearchOrAction(t, defaultE),
                OnRightClick = DoQuickDefaultSearchPicker
            });
        }

        if (editable)
        {
            acts.Add(new PopupActionItem
            {
                Tip = "Cut",
                Glyph = "\uE8C6",
                IsIconGlyph = true,
                Group = 1,
                RelevancePriority = 1,
                OnClick = DoCut
            });
        }

        acts.Add(new PopupActionItem
        {
            Tip = "Copy",
            Glyph = "\uE8C8",
            IsIconGlyph = true,
            Group = 1,
            RelevancePriority = 1,
            OnClick = DoCopy,
            OnRightClick = DoAppendClipboard
        });

        if (!clipboardMode)
        {
            acts.Add(new PopupActionItem
            {
                Tip = "Append to clipboard",
                ShortLabel = "+Copy",
                Glyph = "\uE710",
                IsIconGlyph = true,
                Group = 1,
                RelevancePriority = 2,
                OnClick = DoAppendClipboard,
                OnRightClick = DoAppendClipboard
            });
        }

        if (editable)
        {
            acts.Add(new PopupActionItem
            {
                Tip = "Paste",
                Glyph = "\uE77F",
                IsIconGlyph = true,
                Group = 1,
                RelevancePriority = 1,
                OnClick = DoPaste,
                OnRightClick = ShowClipboardHistoryMenu
            });
        }

        if (editable && clipboardMode)
        {
            acts.Add(new PopupActionItem
            {
                Tip = "Type clipboard",
                Glyph = "\uE8A2",
                IsIconGlyph = true,
                Group = 1,
                RelevancePriority = 2,
                OnClick = DoTypeClipboard
            });
        }

        if (isUrl)
        {
            acts.Add(new PopupActionItem { Tip = "Open link", Glyph = "\uE8A7", IsIconGlyph = true, Group = 2, RelevancePriority = 0, OnClick = DoOpenLink });
            acts.Add(new PopupActionItem { Tip = "Clean URL", Glyph = "\uE8B7", IsIconGlyph = true, Group = 2, RelevancePriority = 0, OnClick = () => CopyTextAction(SmartParser.CleanUrl(t), "Clean URL copied") });
            acts.Add(new PopupActionItem { Tip = "Copy domain", Glyph = "\uE774", IsIconGlyph = true, Group = 2, RelevancePriority = 0, OnClick = () => DoCopyDomain(t) });
            acts.Add(new PopupActionItem
            {
                Tip = "Save URL as action",
                ShortLabel = "+Search",
                Glyph = "\uE710",
                IsIconGlyph = true,
                Accent = true,
                Group = 2,
                RelevancePriority = 0,
                OnClick = () => ShowUrlBuilder(t)
            });
        }
        else if (isEmail)
        {
            acts.Add(new PopupActionItem
            {
                Tip = "New email",
                Glyph = "\uE715",
                IsIconGlyph = true,
                Group = 2,
                RelevancePriority = 0,
                OnClick = () => { HidePopup(); TryShellStart("mailto:" + t); }
            });
        }
        else if (isPath)
        {
            acts.Add(new PopupActionItem { Tip = "Open", Glyph = "\uE8E5", IsIconGlyph = true, Group = 2, RelevancePriority = 0, OnClick = () => DoOpenPath(t) });
            acts.Add(new PopupActionItem
            {
                Tip = "Show in folder",
                Glyph = "\uE838",
                IsIconGlyph = true,
                Group = 2,
                RelevancePriority = 0,
                OnClick = () =>
                {
                    HidePopup();
                    try { Process.Start("explorer.exe", $"/select,\"{t.Trim('\"')}\""); } catch { }
                }
            });
        }
        else if (SmartParser.TryParseHexColor(t, out string hexCol, out var swatchColor))
        {
            var col = swatchColor;
            acts.Add(new PopupActionItem
            {
                Tip = "Copy #" + hexCol + " (Right-click for RGB/HSL)",
                SwatchColor = col,
                Group = 2,
                RelevancePriority = 0,
                OnClick = () => CopyTextAction("#" + hexCol, $"Color #{hexCol} copied"),
                OnRightClick = () => ShowColorVariantsMenu(col)
            });
        }
        else if (SmartParser.TrySmartConvert(t, out string convShort, out string convCopy, out string convTip))
        {
            acts.Add(new PopupActionItem
            {
                Tip = "Copy conversion: " + convTip,
                ShortLabel = convShort,
                Glyph = convShort,
                ForceText = true,
                Accent = true,
                Group = 2,
                RelevancePriority = 0,
                OnClick = () => CopyTextAction(convCopy, "Copied: " + convCopy)
            });
        }
        else if (SmartParser.TryEvalMath(t, out string mathRes))
        {
            acts.Add(new PopupActionItem
            {
                Tip = "Copy result: " + mathRes,
                ShortLabel = "= " + mathRes,
                Glyph = "= " + mathRes,
                ForceText = true,
                Accent = true,
                Group = 2,
                RelevancePriority = 0,
                OnClick = () => CopyTextAction(mathRes, "Copied: " + mathRes)
            });
        }
        else if (Regex.IsMatch(t, @"^[\p{L}'-]{2,30}(?:\s+[\p{L}'-]{2,25})?$"))
        {
            acts.Add(new PopupActionItem
            {
                Tip = "Define",
                Glyph = "\uE82D",
                IsIconGlyph = true,
                Group = 2,
                RelevancePriority = 0,
                OnClick = () => DoInAppCard(ResultCardMode.Define, t),
                OnRightClick = () => { HidePopup(); OpenUrl(ConfigService.DefineUrl + Uri.EscapeDataString(t)); }
            });
        }

        acts.Add(new PopupActionItem
        {
            Tip = "QR code",
            Glyph = "\uED14",
            IsIconGlyph = true,
            Group = 3,
            RelevancePriority = 3,
            OnClick = () => DoQrCode(t)
        });

        bool hasMoreEngines = false;
        foreach (var e in _cfg.SearchEngines)
        {
            if (defaultE != null && string.Equals(e.Name, defaultE.Name, StringComparison.OrdinalIgnoreCase))
                continue;
            if (e.Pin)
            {
                var eng = e;
                bool hasGlyph = !string.IsNullOrEmpty(eng.Glyph);
                acts.Add(new PopupActionItem
                {
                    Tip = "Search " + eng.Name,
                    ShortLabel = eng.Name,
                    Glyph = hasGlyph ? eng.Glyph : (eng.Name.Length >= 2 ? eng.Name[..2] : eng.Name),
                    IsIconGlyph = hasGlyph,
                    Image = _cfg.GetFaviconBitmap(eng),
                    Group = 4,
                    RelevancePriority = 2,
                    OnClick = () => RunSearchOrAction(t, eng),
                    OnRightClick = () => SetDefaultSearchEngineLive(eng.Name, notify: true)
                });
            }
            else
            {
                hasMoreEngines = true;
            }
        }

        if (hasMoreEngines || isUrl)
        {
            acts.Add(new PopupActionItem
            {
                Tip = "Search with ...",
                Glyph = "\uE712",
                IsIconGlyph = true,
                Group = 4,
                RelevancePriority = 3,
                OnClick = DoSearchMenu
            });
        }

        acts.Add(new PopupActionItem
        {
            Tip = "Translate",
            ShortLabel = "Translate",
            Glyph = "\uE8C1",
            IsIconGlyph = true,
            Group = 4,
            RelevancePriority = 2,
            OnClick = () => DoInAppCard(ResultCardMode.Translate, t),
            OnRightClick = () => DoInAppCard(ResultCardMode.Translate, t, expandLangPicker: true)
        });

        SmartParser.GetTextStats(t, out int words, out int chars, out int lines);
        acts.Add(new PopupActionItem
        {
            Tip = $"Words: {words} | Chars: {chars}",
            ShortLabel = $"{words}w {chars}c",
            Glyph = $"{words}w",
            ForceText = true,
            Group = 5,
            RelevancePriority = 3,
            OnClick = () => DoInAppCard(ResultCardMode.Inspect, t),
            OnRightClick = () => CopyTextAction($"{words} words, {chars} characters, {lines} lines", "Stats copied")
        });

        acts.Add(new PopupActionItem
        {
            Tip = "Change case / clean up",
            ShortLabel = "Aa",
            Glyph = "Aa",
            Accent = true,
            Group = 5,
            RelevancePriority = 2,
            OnClick = DoCaseMenu
        });

        var enabledActs = acts.Where(a => _cfg.IsActionEnabled(a.Tip) ||
            (_cfg.IsNeonTui && a.Tip.StartsWith("Words:", StringComparison.OrdinalIgnoreCase))).ToList();
        return ApplyPopupOrder(enabledActs);
    }

    private List<PopupActionItem> ApplyPopupOrder(List<PopupActionItem> acts)
    {
        var result = new List<PopupActionItem>(acts.Count);
        var used = new bool[acts.Count];

        foreach (string key in _cfg.PopupOrder)
        {
            for (int i = 0; i < acts.Count; i++)
            {
                if (!used[i] && string.Equals(acts[i].Tip, key, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(acts[i]);
                    used[i] = true;
                    break;
                }
            }
        }
        for (int i = 0; i < acts.Count; i++)
            if (!used[i])
                result.Add(acts[i]);

        return result;
    }

    private void ReorderPopupItem(string fromTip, string toTip)
    {
        if (!IsPopupOpen) return;
        var order = _popupWindow.VisibleTips.ToList();
        int from = order.FindIndex(t => string.Equals(t, fromTip, StringComparison.OrdinalIgnoreCase));
        int target = order.FindIndex(t => string.Equals(t, toTip, StringComparison.OrdinalIgnoreCase));
        if (from < 0 || target < 0 || from == target) return;

        string item = order[from];
        order.RemoveAt(from);
        if (from < target) target--;
        order.Insert(target, item);

        var visible = new HashSet<string>(order, StringComparer.OrdinalIgnoreCase);
        var merged = new List<string>(order);

        for (int i = 0; i < _cfg.PopupOrder.Count; i++)
        {
            string key = _cfg.PopupOrder[i];
            if (visible.Contains(key)) continue;
            int pos = -1;
            for (int back = i - 1; back >= 0; back--)
            {
                string prevKey = _cfg.PopupOrder[back];
                int foundIdx = merged.FindIndex(k => string.Equals(k, prevKey, StringComparison.OrdinalIgnoreCase));
                if (foundIdx >= 0) { pos = foundIdx; break; }
            }
            merged.Insert(pos + 1, key);
        }

        _cfg.PopupOrder = merged;
        _cfg.SavePopupOrder();

        ShowPopup(_selText, _popupEditable, _popupClipboardMode, instant: true);
    }

    // ---------------- Action Implementations ----------------
    private void CopyTextAction(string txt, string toastMsg)
    {
        DismissAllMenus();
        UiaHelper.SetClipboardUnicodeText(txt);
        _cfg.RecordClipboardItem(txt);
        ShowToast(toastMsg);
    }

    private void DoCut()
    {
        _cfg.RecordClipboardItem(_selText);
        HidePopup();
        UiaHelper.SendCtrlKey('X');
    }

    private void DoCopy()
    {
        if (Native.IsKeyDown(Native.VK_SHIFT) || Native.IsKeyDown(Native.VK_LSHIFT) || Native.IsKeyDown(Native.VK_RSHIFT))
        {
            DoAppendClipboard();
            return;
        }
        bool cm = _popupClipboardMode;
        _cfg.RecordClipboardItem(_selText);
        HidePopup();
        if (!cm)
            UiaHelper.SendCtrlKey('C');
        ShowToast("Copied");
    }

    private void DoAppendClipboard()
    {
        string textToAdd = (_selText ?? "").Trim('\r', '\n');
        HidePopup();
        if (string.IsNullOrEmpty(textToAdd)) return;

        string existing = UiaHelper.GetClipboardUnicodeText();
        if (string.IsNullOrWhiteSpace(existing))
        {
            UiaHelper.SetClipboardUnicodeText(textToAdd);
            _cfg.RecordClipboardItem(textToAdd);
            ShowToast("Copied to clipboard");
        }
        else
        {
            string combined = existing.TrimEnd('\r', '\n') + "\r\n" + textToAdd;
            UiaHelper.SetClipboardUnicodeText(combined);
            _cfg.RecordClipboardItem(textToAdd);
            _cfg.RecordClipboardItem(combined);
            int lines = combined.Count(c => c == '\n') + 1;
            ShowToast($"Appended ({lines} lines in clipboard)");
        }
    }

    private async void DoPaste()
    {
        HidePopup();
        await Task.Delay(35);
        UiaHelper.SendCtrlKey('V');
    }

    private async void DoTypeClipboard()
    {
        string text = Regex.Replace(_selText ?? "", @"\R", "\n");
        int delayMs = _cfg.TypeDelayMs;
        HidePopup();
        await Task.Delay(50);

        _typingActive = true;
        ShowToast("Typing... press Esc to stop", "info", 600000);

        await Task.Run(() =>
        {
            foreach (char ch in text)
            {
                if (!_typingActive) break;
                UiaHelper.SendUnicodeChar(ch);
                if (delayMs > 0)
                    Thread.Sleep(delayMs);
            }
        });

        _typingActive = false;
        _toastWindow?.HideToast();
    }

    private void DoOpenLink()
    {
        string u = (_selText ?? "").Trim();
        HidePopup();
        OpenUrl(Regex.IsMatch(u, @"^https?://", RegexOptions.IgnoreCase) ? u : "https://" + u);
    }

    private void DoCopyDomain(string url)
    {
        var m = Regex.Match((url ?? "").Trim(), @"^(?:https?://)?([^/?:#]+)", RegexOptions.IgnoreCase);
        if (m.Success)
            CopyTextAction(m.Groups[1].Value, "Domain copied: " + m.Groups[1].Value);
    }

    private void DoOpenPath(string rawPath)
    {
        string p = (rawPath ?? "").Trim().Trim('"');
        HidePopup();
        if (Regex.IsMatch(p, @"\.(exe|bat|cmd|com|ps1|psm1|vbs|vbe|js|jse|wsf|wsh|msi|msc|scr|lnk|hta|reg|cpl|jar)$", RegexOptions.IgnoreCase))
        {
            if (MessageBox.Show($"This will run a program or script:\n\n{p}\n\nContinue?", "SparkBar", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
        }
        TryShellStart(p);
    }

    private void DoInAppCard(ResultCardMode mode, string text, bool expandLangPicker = false)
    {
        bool editable = _popupEditable;
        var primaryRect = IsPopupOpen ? _popupWindow.LastScreenRect : _lastPopupRect;
        IntPtr prevWin = _lastActiveWin != IntPtr.Zero ? _lastActiveWin : Native.GetForegroundWindow();
        HidePopup();
        HideOptionMenu();
        HideQr();
        _resultCardWindow?.ShowCard(mode, text, editable, prevWin, _popX, _popY, expandLangPicker, primaryRect);
    }

    private void DoQrCode(string text)
    {
        var primaryRect = (IsPopupOpen || IsOptionMenuOpen || IsResultCardOpen) ? _lastPopupRect : default;
        DismissAllMenus();
        HideQr();
        if (text.Length > _cfg.QrMaxChars)
        {
            ShowNotice($"Too long for a QR code ({text.Length} characters, limit {_cfg.QrMaxChars})");
            return;
        }

        var bmp = SmartParser.GenerateQrBitmap(text, 320);
        if (bmp == null)
        {
            ShowNotice("Unable to generate QR code for the selected text.");
            return;
        }

        _qrWindow = new QrWindow(
            _cfg,
            bmp,
            text,
            msg => ShowToast(msg),
            !string.IsNullOrWhiteSpace(_selText) ? ReturnToMainPopup : null);
        _qrWindow.ShowAt(_popX, _popY, primaryRect);
    }

    private void RunSearchOrAction(string text, SearchEngineEntry item)
    {
        DismissAllMenus();
        string enc = Uri.EscapeDataString(text ?? "");
        string url = item.Url.Contains("{selection}", StringComparison.Ordinal)
            ? item.Url.Replace("{selection}", enc)
            : item.Url + enc;
        OpenUrl(url);
    }

    private void OpenUrl(string url)
    {
        if (!SmartParser.IsAllowedActionUri(url))
            ShowNotice("Blocked: unsupported or unsafe URI scheme");
        else if (url.Length > _cfg.MaxUrlLen)
            ShowNotice($"Too long for a link ({url.Length} characters, limit {_cfg.MaxUrlLen})");
        else if (!TryShellStart(url))
            ShowNotice("Could not open the link or application");
    }

    private static bool TryShellStart(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return true;
        }
        catch { return false; }
    }

    private void DoQuickDefaultSearchPicker()
    {
        string t = (_selText ?? "").Trim();
        HidePopup();
        var items = new List<OptionMenuItem>();
        foreach (var e in _cfg.SearchEngines)
        {
            var eng = e;
            bool isDef = string.Equals(eng.Name, _cfg.DefaultSearchEngine, StringComparison.OrdinalIgnoreCase);
            items.Add(new OptionMenuItem
            {
                Label = (isDef ? "★ " : "") + eng.Name + (isDef ? "  (Current Default)" : ""),
                Accent = isDef,
                Image = _cfg.GetFaviconBitmap(eng),
                Glyph = eng.Name.Length >= 2 ? eng.Name[..2] : eng.Name,
                OnClick = () =>
                {
                    SetDefaultSearchEngineLive(eng.Name, notify: false);
                    RunSearchOrAction(t, eng);
                }
            });
        }
        if (items.Count > 0)
            ShowThemedMenu(items, "SET DEFAULT & SEARCH");
    }

    private void DoSearchMenu()
    {
        string t = (_selText ?? "").Trim();
        HidePopup();
        var items = new List<OptionMenuItem>();

        if (Regex.IsMatch(t, @"^(https?://|www\.)\S+$", RegexOptions.IgnoreCase))
        {
            items.Add(new OptionMenuItem
            {
                Label = "Add URL as Search Engine...",
                Glyph = "+",
                Accent = true,
                OnClick = () => ShowUrlBuilder(t)
            });
        }

        foreach (var e in _cfg.SearchEngines)
        {
            if (!e.Pin && !string.Equals(e.Name, _cfg.DefaultSearchEngine, StringComparison.OrdinalIgnoreCase))
            {
                var eng = e;
                items.Add(new OptionMenuItem
                {
                    Label = eng.Name,
                    Image = _cfg.GetFaviconBitmap(eng),
                    Glyph = eng.Name.Length >= 2 ? eng.Name[..2] : eng.Name,
                    OnClick = () => RunSearchOrAction(t, eng)
                });
            }
        }

        items.Add(new OptionMenuItem
        {
            Label = $"Default: {_cfg.DefaultSearchEngine}",
            Glyph = "★",
            Accent = true,
            OnClick = DoQuickDefaultSearchPicker
        });

        ShowThemedMenu(items, "SEARCH WITH");
    }

    private void DoCaseMenu()
    {
        string text = _selText ?? "";
        bool editable = _popupEditable;
        IntPtr prevWin = _lastActiveWin != IntPtr.Zero ? _lastActiveWin : Native.GetForegroundWindow();
        HidePopup();

        SmartParser.GetTextStats(text, out int words, out int chars, out int lines);
        string statsLabel = $"{words} words  ·  {chars} chars" + (lines > 1 ? $"  ·  {lines} lines" : "");

        var items = new List<OptionMenuItem>
        {
            new() { Label = statsLabel,            Glyph = "#",  Accent = true, OnClick = () => CopyTextAction(statsLabel, "Stats copied") },
            new() { Label = "UPPERCASE",           Glyph = "AA", OnClick = () => ApplyTransform(prevWin, text, editable, s => s.ToUpper()) },
            new() { Label = "lowercase",           Glyph = "aa", OnClick = () => ApplyTransform(prevWin, text, editable, s => s.ToLower()) },
            new() { Label = "Title Case",          Glyph = "Tt", OnClick = () => ApplyTransform(prevWin, text, editable, SmartParser.ToTitleCase) },
            new() { Label = "Sentence case",       Glyph = "Ss", OnClick = () => ApplyTransform(prevWin, text, editable, SmartParser.ToSentenceCase) },
            new() { Label = "camelCase",           Glyph = "cC", OnClick = () => ApplyTransform(prevWin, text, editable, SmartParser.ToCamelCase) },
            new() { Label = "PascalCase",          Glyph = "PC", OnClick = () => ApplyTransform(prevWin, text, editable, SmartParser.ToPascalCase) },
            new() { Label = "snake_case",          Glyph = "s_", OnClick = () => ApplyTransform(prevWin, text, editable, SmartParser.ToSnakeCase) },
            new() { Label = "kebab-case",          Glyph = "k-", OnClick = () => ApplyTransform(prevWin, text, editable, SmartParser.ToKebabCase) },
            new() { Label = "CONSTANT_CASE",       Glyph = "C_", OnClick = () => ApplyTransform(prevWin, text, editable, SmartParser.ToConstantCase) },
            new() { Label = "Wrap \"quotes\"",     Glyph = "\"\"", OnClick = () => ApplyTransform(prevWin, text, editable, s => "\"" + s + "\"") },
            new() { Label = "Clean spaces",        Glyph = "↔",  OnClick = () => ApplyTransform(prevWin, text, editable, s => Regex.Replace(s.Trim(), @"\s+", " ")) },
            new() { Label = "Join lines",          Glyph = "↵",  OnClick = () => ApplyTransform(prevWin, text, editable, s => Regex.Replace(s.Trim(), @"\s*\R\s*", " ")) }
        };

        string clipUrl = UiaHelper.GetClipboardUnicodeText().Trim();
        if (Regex.IsMatch(clipUrl, @"^https?://\S+$", RegexOptions.IgnoreCase) && !Regex.IsMatch(text.Trim(), @"^https?://\S+$", RegexOptions.IgnoreCase))
        {
            items.Add(new OptionMenuItem
            {
                Label = "Wrap as Markdown [text](url)",
                Glyph = "[]",
                Accent = true,
                OnClick = () => ApplyTransform(prevWin, text, editable, s => $"[{s.Trim()}]({clipUrl})")
            });
        }

        if (SmartParser.TryFormatJson(text, indented: true, out string prettyJson))
        {
            SmartParser.TryFormatJson(text, indented: false, out string minJson);
            items.Add(new OptionMenuItem { Label = "Format JSON (Pretty)", Glyph = "{}", Accent = true, OnClick = () => ApplyTransform(prevWin, text, editable, _ => prettyJson) });
            items.Add(new OptionMenuItem { Label = "Minify JSON",          Glyph = "{·}", OnClick = () => ApplyTransform(prevWin, text, editable, _ => minJson) });
        }

        if (SmartParser.TryDecodeBase64(text, out string decodedB64))
        {
            string preview = decodedB64.Length > 24 ? decodedB64[..24] + "…" : decodedB64;
            items.Add(new OptionMenuItem
            {
                Label = $"Decode Base64 → {Regex.Replace(preview, @"\s+", " ")}",
                Glyph = "64",
                Accent = true,
                OnClick = () => ApplyTransform(prevWin, text, editable, _ => decodedB64)
            });
        }

        if (lines > 1)
        {
            items.Add(new OptionMenuItem { Label = "Sort lines A-Z",        Glyph = "↓Z", OnClick = () => ApplyTransform(prevWin, text, editable, SmartParser.SortLinesAZ) });
            items.Add(new OptionMenuItem { Label = "Trim & dedupe lines",   Glyph = "≡",  OnClick = () => ApplyTransform(prevWin, text, editable, SmartParser.DedupeLines) });
            items.Add(new OptionMenuItem { Label = "Number lines (1. 2.)",  Glyph = "1.", OnClick = () => ApplyTransform(prevWin, text, editable, SmartParser.NumberLines) });
            items.Add(new OptionMenuItem { Label = "Bullet lines (•)",      Glyph = "•",  OnClick = () => ApplyTransform(prevWin, text, editable, SmartParser.BulletLines) });
        }

        ShowThemedMenu(items, "TRANSFORM TEXT");
    }

    private void ShowColorVariantsMenu(System.Windows.Media.Color color)
    {
        HidePopup();
        var (hex, rgb, hsl) = SmartParser.FormatColorVariants(color);
        var items = new List<OptionMenuItem>
        {
            new() { Label = $"HEX   {hex}", Glyph = "#",   Accent = true, OnClick = () => CopyTextAction(hex, $"Copied {hex}") },
            new() { Label = $"RGB   {rgb}", Glyph = "RGB", OnClick = () => CopyTextAction(rgb, $"Copied {rgb}") },
            new() { Label = $"HSL   {hsl}", Glyph = "HSL", OnClick = () => CopyTextAction(hsl, $"Copied {hsl}") }
        };
        ShowThemedMenu(items, "COPY COLOR FORMAT");
    }

    private void ShowClipboardHistoryMenu()
    {
        bool fromPopup = IsPopupOpen;
        var savedRect = fromPopup ? _popupWindow.LastScreenRect : default;
        DismissAllMenus();
        _lastPopupRect = fromPopup ? savedRect : default;

        string curClip = UiaHelper.GetClipboardUnicodeText();
        if (!string.IsNullOrWhiteSpace(curClip))
            _cfg.RecordClipboardItem(curClip);

        if (_popX == 0 && _popY == 0)
            UiaHelper.ResolveKeyboardAnchor(out _popX, out _popY);
        else
        {
            Native.GetCursorPos(out var cur);
            _popX = cur.X;
            _popY = cur.Y;
        }

        var hist = _cfg.ClipboardHistory;
        if (hist.Count == 0)
        {
            ShowToast("Clipboard history is empty", "info", 1400);
            return;
        }

        IntPtr targetWin = Native.GetForegroundWindow();
        bool editable = UiaHelper.IsEditableFocus(_cfg.ShowEditWhenUnknown);
        var items = new List<OptionMenuItem>();

        for (int i = 0; i < hist.Count; i++)
        {
            string clip = hist[i];
            string oneLine = Regex.Replace(clip, @"\s+", " ").Trim();
            if (oneLine.Length > 42) oneLine = oneLine[..41] + "…";
            items.Add(new OptionMenuItem
            {
                Label = oneLine,
                Glyph = (i == 0) ? "★" : "📋",
                Accent = (i == 0),
                OnClick = () =>
                {
                    UiaHelper.SetClipboardUnicodeText(clip);
                    _cfg.RecordClipboardItem(clip);
                    if (editable && targetWin != IntPtr.Zero && Native.IsWindow(targetWin))
                    {
                        Native.SetForegroundWindow(targetWin);
                        Task.Delay(35).ContinueWith(_ => UiaHelper.SendCtrlKey('V'));
                    }
                    else
                    {
                        ShowToast("Copied from history");
                    }
                }
            });
        }

        if (hist.Count > 1)
        {
            items.Add(new OptionMenuItem
            {
                Label = $"Copy All {hist.Count} Clips Joined",
                Glyph = "+",
                Accent = true,
                OnClick = () => CopyTextAction(string.Join("\r\n", hist), $"Joined {hist.Count} clips")
            });
            items.Add(new OptionMenuItem
            {
                Label = "Clear Clipboard History",
                Glyph = "×",
                OnClick = () => { _cfg.ClearClipboardHistory(); ShowToast("Clipboard history cleared", "info"); }
            });
        }

        ShowThemedMenu(items, "CLIPBOARD HISTORY", allowBack: hist.Count > 0 && fromPopup, useLastPopupRect: fromPopup);
    }

    private async void ApplyTransform(IntPtr targetWin, string text, bool editable, Func<string, string> transform)
    {
        string outText = transform(text ?? "");
        _cfg.RecordClipboardItem(outText);
        if (!editable)
        {
            UiaHelper.SetClipboardUnicodeText(outText);
            ShowToast("Transformed & copied");
            return;
        }

        string oldClip = UiaHelper.GetClipboardUnicodeText();
        if (targetWin != IntPtr.Zero && Native.IsWindow(targetWin))
            Native.SetForegroundWindow(targetWin);

        UiaHelper.SetClipboardUnicodeText(outText);
        await Task.Delay(35);
        UiaHelper.SendCtrlKey('V');
        await Task.Delay(250);
        if (!string.IsNullOrEmpty(oldClip))
            UiaHelper.SetClipboardUnicodeText(oldClip, excludeFromHistory: true);
    }

    // ---------------- URL Builder, Settings & Tray Actions ----------------
    private void ShowUrlBuilder(string rawUrl)
    {
        DismissAllMenus();
        ApplyFluentAppTheme();
        if (_urlBuilderWindow != null)
        {
            try { _urlBuilderWindow.Close(); } catch { }
            _urlBuilderWindow = null;
        }
        _urlBuilderWindow = new UrlBuilderWindow(_cfg, rawUrl, (name, tpl, isPin, makeDef) =>
        {
            var existing = _cfg.SearchEngines.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.Url = tpl;
                existing.Pin = isPin;
            }
            else
            {
                var ne = new SearchEngineEntry { Name = name, Url = tpl, Pin = isPin };
                string fav = _cfg.EnsureFavicon(ne);
                if (!string.IsNullOrEmpty(fav)) ne.FaviconPath = fav;
                else _cfg.QueueFavicon(ne);
                _cfg.SearchEngines.Add(ne);
            }
            _cfg.SaveSearchEngines();
            if (makeDef)
                SetDefaultSearchEngineLive(name, notify: false);
            _settingsWindow?.RefreshExternalChanges();
            ShowToast((makeDef ? "Saved as Default Search: " : "Saved search engine: ") + name);
        });
        _urlBuilderWindow.Closed += (_, _) => _urlBuilderWindow = null;
        _urlBuilderWindow.Show();
    }

    private void SetDefaultSearchEngineLive(string name, bool notify)
    {
        name = (name ?? "").Trim();
        if (string.IsNullOrEmpty(name)) return;
        _cfg.DefaultSearchEngine = name;
        _cfg.WriteIni("General", "DefaultSearchEngine", name);
        _settingsWindow?.RefreshExternalChanges();
        if (notify)
            ShowToast("Default search: " + name);
    }

    private void ToggleEnabled()
    {
        _enabled = !_enabled;
        if (!_enabled)
            DismissAllMenus();
        ShowToast(_enabled ? "SparkBar enabled" : "SparkBar paused", _enabled ? "ok" : "info", 1300);
    }

    private void ToggleExcludeActiveApp()
    {
        IntPtr fg = Native.GetForegroundWindow();
        string exe = Native.ProcessNameOf(fg);
        if (string.IsNullOrEmpty(exe) || Native.IsOwnWindow(fg) || string.Equals(exe, "SparkBar.exe", StringComparison.OrdinalIgnoreCase))
        {
            ShowNotice("Switch to the target app first, then press Ctrl+Alt+X");
            return;
        }

        if (_cfg.ExcludedExes.Remove(exe))
        {
            _cfg.SaveExcludedApps();
            ShowToast("Re-enabled SparkBar in " + exe, "info", 1600);
        }
        else
        {
            _cfg.ExcludedExes.Add(exe);
            _cfg.SaveExcludedApps();
            DismissAllMenus();
            ShowToast("Excluded " + exe + " from SparkBar", "warn", 1600);
        }
        _settingsWindow?.RefreshExternalChanges();
    }

    private void ShowSettingsWindow()
    {
        ApplyFluentAppTheme();
        if (_settingsWindow != null)
        {
            if (_settingsWindow.WindowState == WindowState.Minimized)
                _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(_cfg, onSettingsChanged: ApplyFluentAppTheme);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void OpenErrorLog()
    {
        if (!File.Exists(_cfg.ErrorLogFile))
            ConfigService.WriteFileAtomic(_cfg.ErrorLogFile, $"SparkBar error log created: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
        TryShellStart(_cfg.ErrorLogFile);
    }

    private void ReloadAppConfiguration()
    {
        DismissAllMenus(instant: true);
        _cfg.ReloadAll();
        ApplyFluentAppTheme();
        _settingsWindow?.RefreshExternalChanges();
        ShowToast("SparkBar configuration reloaded", "info", 1300);
    }

    private void ExitApplication()
    {
        _shuttingDown = true;
        _selSignal.Set();
        DismissAllMenus(instant: true);
        HideQr();
        try { _popupWindow?.Close(); } catch { }
        try { _optionMenuWindow?.Close(); } catch { }
        try { _resultCardWindow?.Close(); } catch { }
        try { _toastWindow?.Close(); } catch { }
        if (_winEventHook != IntPtr.Zero)
        {
            Native.UnhookWinEvent(_winEventHook);
            _winEventHook = IntPtr.Zero;
        }
        _hooks?.Dispose();
        _tray?.Dispose();
        Shutdown();
        Environment.Exit(0);
    }
}

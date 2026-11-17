using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace SparkBar;

/// <summary>
/// Pure Win32 Shell_NotifyIcon system tray manager with a custom Windows 11 Taskbar Flyout menu.
/// </summary>
internal sealed class TrayIconManager : IDisposable
{
    private const uint WM_TRAYICON = 0x8000 + 42;
    private const uint TrayUid = 1001;

    private readonly ConfigService _cfg;
    private readonly Func<bool> _getEnabled;
    private readonly Action _onToggleEnabled;
    private readonly Action<string> _onSelectDefaultEngine;
    private readonly Action _onToggleExcludeActiveApp;
    private readonly Action _onOpenSettings;
    private readonly Action _onOpenClipboardHistory;
    private readonly Action _onOpenErrorLog;
    private readonly Action _onReload;
    private readonly Action _onExit;
    private readonly Action _onSystemThemeChanged;

    private HwndSource _sink;
    private IntPtr _hIcon;
    private uint _wmTaskbarCreated;
    private TrayFlyoutWindow _flyout;

    public IntPtr FlyoutHwnd => _flyout?.Hwnd ?? IntPtr.Zero;
    public bool IsFlyoutOpen => _flyout != null;

    public TrayIconManager(
        ConfigService cfg,
        Func<bool> getEnabled,
        Action onToggleEnabled,
        Action<string> onSelectDefaultEngine,
        Action onToggleExcludeActiveApp,
        Action onOpenSettings,
        Action onOpenClipboardHistory,
        Action onOpenErrorLog,
        Action onReload,
        Action onExit,
        Action onSystemThemeChanged = null)
    {
        _cfg = cfg;
        _getEnabled = getEnabled;
        _onToggleEnabled = onToggleEnabled;
        _onSelectDefaultEngine = onSelectDefaultEngine;
        _onToggleExcludeActiveApp = onToggleExcludeActiveApp;
        _onOpenSettings = onOpenSettings;
        _onOpenClipboardHistory = onOpenClipboardHistory;
        _onOpenErrorLog = onOpenErrorLog;
        _onReload = onReload;
        _onExit = onExit;
        _onSystemThemeChanged = onSystemThemeChanged;

        _wmTaskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");

        var p = new HwndSourceParameters("SparkBarTraySink")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = 0
        };
        _sink = new HwndSource(p);
        _sink.AddHook(WndProc);

        _hIcon = LoadTrayIcon();
        AddNotifyIcon();
    }

    private IntPtr LoadTrayIcon()
    {
        try
        {
            string exePath = Environment.ProcessPath ?? Path.Combine(_cfg.BaseDir, "SparkBar.exe");
            IntPtr[] small = new IntPtr[1];
            IntPtr[] large = new IntPtr[1];
            if (File.Exists(exePath) && Native.ExtractIconEx(exePath, 0, large, small, 1) > 0)
            {
                if (large[0] != IntPtr.Zero && small[0] != IntPtr.Zero)
                    Native.DestroyIcon(large[0]);
                if (small[0] != IntPtr.Zero) return small[0];
                if (large[0] != IntPtr.Zero) return large[0];
            }
        }
        catch { }
        return Native.LoadIcon(IntPtr.Zero, new IntPtr(32512 /*IDI_APPLICATION*/));
    }

    private void AddNotifyIcon()
    {
        if (_sink == null) return;
        var nid = new Native.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<Native.NOTIFYICONDATA>(),
            hWnd = _sink.Handle,
            uID = TrayUid,
            uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _hIcon,
            szTip = "SparkBar (Active)"
        };
        Native.Shell_NotifyIcon(Native.NIM_ADD, ref nid);
    }

    public void UpdateTooltip()
    {
        if (_sink == null) return;
        var nid = new Native.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<Native.NOTIFYICONDATA>(),
            hWnd = _sink.Handle,
            uID = TrayUid,
            uFlags = Native.NIF_TIP,
            szTip = _getEnabled() ? "SparkBar (Active)" : "SparkBar (Paused)"
        };
        Native.Shell_NotifyIcon(Native.NIM_MODIFY, ref nid);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_SETTINGCHANGE || msg == Native.WM_DWMCOLORIZATIONCOLORCHANGED)
        {
            _onSystemThemeChanged?.Invoke();
            return IntPtr.Zero;
        }

        if (_wmTaskbarCreated != 0 && (uint)msg == _wmTaskbarCreated)
        {
            AddNotifyIcon();
            return IntPtr.Zero;
        }

        if ((uint)msg == WM_TRAYICON)
        {
            int ev = lParam.ToInt32() & 0xFFFF;
            if (ev == Native.WM_RBUTTONUP)
            {
                handled = true;
                ShowTrayFlyout();
            }
            else if (ev == 0x0208 /*WM_MBUTTONUP*/)
            {
                handled = true;
                CloseFlyout();
                _onToggleEnabled?.Invoke();
                UpdateTooltip();
            }
            else if (ev == 0x0203 /*WM_LBUTTONDBLCLK*/)
            {
                handled = true;
                CloseFlyout();
                _onOpenSettings?.Invoke();
            }
        }
        return IntPtr.Zero;
    }

    public void CloseFlyout()
    {
        if (_flyout != null)
        {
            var f = _flyout;
            _flyout = null;
            f.Dismiss();
        }
    }

    private void ShowTrayFlyout()
    {
        CloseFlyout();
        Native.GetCursorPos(out var cur);
        _flyout = new TrayFlyoutWindow(
            _cfg,
            _getEnabled(),
            onToggleEnabled: () => { CloseFlyout(); _onToggleEnabled?.Invoke(); UpdateTooltip(); },
            onSelectDefaultEngine: eng => { CloseFlyout(); _onSelectDefaultEngine?.Invoke(eng); },
            onToggleExcludeActiveApp: () => { CloseFlyout(); _onToggleExcludeActiveApp?.Invoke(); },
            onOpenClipboardHistory: () => { CloseFlyout(); _onOpenClipboardHistory?.Invoke(); },
            onOpenSettings: () => { CloseFlyout(); _onOpenSettings?.Invoke(); },
            onOpenErrorLog: () => { CloseFlyout(); _onOpenErrorLog?.Invoke(); },
            onReload: () => { CloseFlyout(); _onReload?.Invoke(); },
            onExit: () => { CloseFlyout(); _onExit?.Invoke(); });
        _flyout.Closed += (_, _) => _flyout = null;
        _flyout.ShowNearTray(cur.X, cur.Y);
    }

    public void Dispose()
    {
        CloseFlyout();
        if (_sink != null)
        {
            var nid = new Native.NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<Native.NOTIFYICONDATA>(),
                hWnd = _sink.Handle,
                uID = TrayUid
            };
            Native.Shell_NotifyIcon(Native.NIM_DELETE, ref nid);
            _sink.Dispose();
            _sink = null;
        }
        if (_hIcon != IntPtr.Zero)
        {
            Native.DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }
    }
}

internal sealed class TrayFlyoutWindow : Window
{
    private readonly ConfigService _cfg;
    private readonly Border _card;
    private bool _closing;

    public IntPtr Hwnd => new WindowInteropHelper(this).Handle;

    public TrayFlyoutWindow(
        ConfigService cfg,
        bool isEnabled,
        Action onToggleEnabled,
        Action<string> onSelectDefaultEngine,
        Action onToggleExcludeActiveApp,
        Action onOpenClipboardHistory,
        Action onOpenSettings,
        Action onOpenErrorLog,
        Action onReload,
        Action onExit)
    {
        _cfg = cfg;
        var pal = cfg.UpdatePalette();

        W11WindowHelper.ConfigureDwmFlyoutChrome(this);

        var stack = new StackPanel { Orientation = Orientation.Vertical, Width = 256 };
        var hoverBg = new LinearGradientBrush(pal.HoverTop, pal.HoverBottom, 90);
        var hoverBorder = new SolidColorBrush(pal.HoverBorder.Value);

        var hdr = new DockPanel { Margin = new Thickness(10, 4, 10, 6) };
        hdr.Children.Add(new TextBlock
        {
            Text = "SparkBar",
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Foreground = new SolidColorBrush(pal.Text),
            VerticalAlignment = VerticalAlignment.Center
        });
        var statusPill = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 1),
            Background = new SolidColorBrush(isEnabled
                ? (pal.IsLight ? Color.FromRgb(0xDC, 0xFC, 0xE7) : Color.FromRgb(0x13, 0x2D, 0x24))
                : pal.Surface),
            BorderBrush = new SolidColorBrush(isEnabled
                ? (pal.IsLight ? Color.FromRgb(0x16, 0xA3, 0x4A) : Color.FromRgb(0x34, 0xD3, 0x99))
                : pal.Separator),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right,
            Child = new TextBlock
            {
                Text = isEnabled ? "Active" : "Paused",
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(isEnabled
                    ? (pal.IsLight ? Color.FromRgb(0x16, 0xA3, 0x4A) : Color.FromRgb(0x34, 0xD3, 0x99))
                    : pal.SubText)
            }
        };
        DockPanel.SetDock(statusPill, Dock.Right);
        hdr.Children.Add(statusPill);
        stack.Children.Add(hdr);

        void AddDivider()
        {
            stack.Children.Add(new Border
            {
                Height = 1,
                Background = new SolidColorBrush(pal.Separator),
                Margin = new Thickness(8, 4, 8, 4)
            });
        }

        Border MakeRow(string iconGlyph, string label, string rightBadge, bool bold, Action onClick)
        {
            var g = new Grid { Height = 32 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            g.Children.Add(new TextBlock
            {
                Text = iconGlyph,
                FontFamily = new FontFamily(cfg.IconFontFamily),
                FontSize = 14,
                Foreground = new SolidColorBrush(pal.Text),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });

            var tb = new TextBlock
            {
                Text = label,
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                FontSize = 13,
                Foreground = new SolidColorBrush(pal.Text),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(tb, 1);
            g.Children.Add(tb);

            if (!string.IsNullOrEmpty(rightBadge))
            {
                var badge = new TextBlock
                {
                    Text = rightBadge,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(pal.SubText),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 8, 0)
                };
                Grid.SetColumn(badge, 2);
                g.Children.Add(badge);
            }

            var b = new Border
            {
                CornerRadius = new CornerRadius(6),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 1, 0, 1),
                Cursor = Cursors.Hand,
                Child = g
            };
            b.MouseEnter += (_, _) => { b.Background = hoverBg; b.BorderBrush = hoverBorder; };
            b.MouseLeave += (_, _) => { b.Background = Brushes.Transparent; b.BorderBrush = Brushes.Transparent; };
            b.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick?.Invoke(); };
            return b;
        }

        AddDivider();
        stack.Children.Add(MakeRow(isEnabled ? "\uE73E" : "\uE768", isEnabled ? "Enabled (Click to Pause)" : "Resume SparkBar", "", false, onToggleEnabled));
        stack.Children.Add(MakeRow("\uE8C8", "Clipboard History", "Ctrl+Alt+V", false, onOpenClipboardHistory));
        stack.Children.Add(MakeRow("\uE8F8", "Exclude active app", "Ctrl+Alt+X", false, onToggleExcludeActiveApp));

        var engineSubPanel = new StackPanel { Orientation = Orientation.Vertical, Visibility = Visibility.Collapsed, Margin = new Thickness(12, 2, 4, 2) };
        foreach (var eng in cfg.SearchEngines)
        {
            string engName = eng.Name;
            bool isDef = string.Equals(engName, cfg.DefaultSearchEngine, StringComparison.OrdinalIgnoreCase);
            engineSubPanel.Children.Add(MakeRow(isDef ? "\uE735" : "\uE721", engName, isDef ? "Default" : "", isDef, () => onSelectDefaultEngine(engName)));
        }
        stack.Children.Add(MakeRow("\uE721", $"Default: {cfg.DefaultSearchEngine}", "▾", false, () =>
        {
            engineSubPanel.Visibility = engineSubPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        }));
        stack.Children.Add(engineSubPanel);

        AddDivider();
        stack.Children.Add(MakeRow("\uE713", "Settings...", "", true, onOpenSettings));
        stack.Children.Add(MakeRow("\uE8A5", "Open error log", "", false, onOpenErrorLog));
        AddDivider();
        stack.Children.Add(MakeRow("\uE72C", "Reload configuration", "", false, onReload));
        stack.Children.Add(MakeRow("\uE7E8", "Exit", "", false, onExit));

        _card = new Border
        {
            Padding = new Thickness(6),
            Background = new LinearGradientBrush(pal.BgTop, pal.BgBottom, 90),
            Child = stack
        };

        Content = _card;
    }

    public void ShowNearTray(int screenX, int screenY)
    {
        W11WindowHelper.MakeNonActivatingFlyout(this, _cfg.Palette);
        Show();
        UpdateLayout();

        double scale = Native.ScaleAt(screenX, screenY);
        var work = Native.WorkAreaAt(screenX, screenY, out _);
        int physW = (int)Math.Round(ActualWidth * scale);
        int physH = (int)Math.Round(ActualHeight * scale);
        int pad = (int)Math.Round(6 * scale);

        int px = Math.Max(work.Left + pad, Math.Min(screenX - physW / 2, work.Right - physW - pad));
        int py = Math.Max(work.Top + pad, Math.Min(screenY - physH - (int)Math.Round(8 * scale), work.Bottom - physH - pad));
        Native.MoveWindowPhysical(Hwnd, px, py);

        W11WindowHelper.AnimateFlyoutIn(this, _card, _cfg.FadeInMs, W11WindowHelper.ShouldAnimate(_cfg));
    }

    public void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        W11WindowHelper.AnimateFlyoutOutAndClose(this, _card, _cfg.FadeOutMs, W11WindowHelper.ShouldAnimate(_cfg));
    }
}

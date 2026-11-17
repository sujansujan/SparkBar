using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;

namespace SparkBar;

internal static class W11WindowHelper
{
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_BORDER_COLOR = 34;
    public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    public const int DWMWCP_ROUND = 2;
    public const int DWMWCP_ROUNDSMALL = 3;
    public const int DWMSBT_MAINWINDOW = 2;      // Mica
    public const int DWMSBT_TRANSIENTWINDOW = 3; // Acrylic

    /// <summary>
    /// Configures a borderless window to use native Windows 11 DWM hardware rounded corners, border, and shadow
    /// without WPF's slow AllowsTransparency/UpdateLayeredWindow or DropShadowEffect overhead.
    /// </summary>
    public static void ConfigureDwmFlyoutChrome(Window w)
    {
        w.WindowStyle = WindowStyle.None;
        w.ResizeMode = ResizeMode.NoResize;
        w.ShowInTaskbar = false;
        w.Topmost = true;
        w.ShowActivated = false;
        w.SizeToContent = SizeToContent.WidthAndHeight;
        w.Background = Brushes.Transparent;
        w.UseLayoutRounding = true;
        w.SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(w, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(w, TextRenderingMode.ClearType);
        RenderOptions.SetBitmapScalingMode(w, BitmapScalingMode.HighQuality);

        var chrome = new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false
        };
        WindowChrome.SetWindowChrome(w, chrome);
    }

    public static void MakeNonActivatingFlyout(Window w, ThemePalette pal, bool clickThrough = false, bool smallCorners = false)
    {
        var helper = new WindowInteropHelper(w);
        IntPtr hwnd = helper.EnsureHandle();

        int exAdd = Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST;
        if (clickThrough) exAdd |= Native.WS_EX_TRANSPARENT;
        Native.AddExStyle(hwnd, exAdd);

        w.Background = new SolidColorBrush(pal.BgBottom);
        ApplyDwmFlyoutAttributes(hwnd, pal, smallCorners);

        var src = HwndSource.FromHwnd(hwnd);
        if (src != null)
        {
            src.CompositionTarget.BackgroundColor = pal.BgBottom;
            src.AddHook(NonActivatingWndProc);
        }
    }

    public static void ApplyDwmFlyoutAttributes(IntPtr hwnd, ThemePalette pal, bool smallCorners = false)
    {
        if (hwnd == IntPtr.Zero || pal == null) return;
        Native.SetDwm(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, pal.IsLight ? 0 : 1);
        Native.SetDwm(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, smallCorners ? DWMWCP_ROUNDSMALL : DWMWCP_ROUND);
        int bgrBorder = (pal.Border.B << 16) | (pal.Border.G << 8) | pal.Border.R;
        Native.SetDwm(hwnd, DWMWA_BORDER_COLOR, bgrBorder);
    }

    private static IntPtr NonActivatingWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(Native.MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }

    public static void ApplyMicaWindowTheme(Window w, ThemePalette pal, bool useAcrylic = false)
    {
        var helper = new WindowInteropHelper(w);
        IntPtr hwnd = helper.EnsureHandle();

        Native.SetDwm(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, pal.IsLight ? 0 : 1);
        Native.SetDwm(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND);

        int bgrBorder = (pal.Border.B << 16) | (pal.Border.G << 8) | pal.Border.R;
        Native.SetDwm(hwnd, DWMWA_BORDER_COLOR, bgrBorder);

        var src = HwndSource.FromHwnd(hwnd);
        if (src != null)
            src.CompositionTarget.BackgroundColor = Colors.Transparent;

        if (Native.OsBuild >= 22523)
        {
            var margins = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            Native.DwmExtendFrameIntoClientArea(hwnd, ref margins);
            Native.SetDwm(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, useAcrylic ? DWMSBT_TRANSIENTWINDOW : DWMSBT_MAINWINDOW);
            w.Background = new SolidColorBrush(Color.FromArgb((byte)(pal.IsLight ? 215 : 195), pal.BgBottom.R, pal.BgBottom.G, pal.BgBottom.B));
        }
        else
        {
            w.Background = new SolidColorBrush(pal.BgBottom);
        }
    }

    public static bool ShouldAnimate(ConfigService cfg)
    {
        if (!cfg.AnimationsEnabled) return false;
        int on = 1;
        try { Native.SystemParametersInfo(0x1042 /*SPI_GETCLIENTAREAANIMATION*/, 0, ref on, 0); } catch { }
        return on != 0;
    }

    public static void AnimateFlyoutIn(Window w, UIElement rootCard, int durationMs, bool animate)
    {
        rootCard.BeginAnimation(UIElement.OpacityProperty, null);
        if (!animate || durationMs <= 0)
        {
            rootCard.Opacity = 1.0;
            rootCard.RenderTransform = Transform.Identity;
            return;
        }

        var trans = new TranslateTransform(0, 2.5);
        rootCard.RenderTransform = trans;
        rootCard.Opacity = 0.55;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = new Duration(TimeSpan.FromMilliseconds(durationMs));

        rootCard.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.55, 1.0, dur) { EasingFunction = ease });
        trans.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(2.5, 0.0, dur) { EasingFunction = ease });
    }

    public static void AnimateFlyoutOutAndClose(Window w, UIElement rootCard, int durationMs, bool animate)
    {
        if (w == null) return;
        try { w.Close(); } catch { }
    }
}

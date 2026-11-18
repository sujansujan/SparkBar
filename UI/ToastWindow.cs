using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace SparkBar;

/// <summary>
/// Pre-warmed, persistent toast notification pill window.
/// </summary>
internal sealed class ToastWindow : Window
{
    private readonly ConfigService _cfg;
    private readonly Border _card;
    private readonly Border _badge;
    private readonly TextBlock _badgeGlyph;
    private readonly TextBlock _label;
    private readonly DispatcherTimer _holdTimer;
    private IntPtr _hwnd;

    public ToastWindow(ConfigService cfg)
    {
        _cfg = cfg;
        var pal = cfg.Palette;

        W11WindowHelper.ConfigureDwmFlyoutChrome(this);
        IsHitTestVisible = false;

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        _badgeGlyph = new TextBlock
        {
            FontFamily = new FontFamily(cfg.IconFontFamily),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        _badge = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 9, 0),
            Child = _badgeGlyph
        };

        _label = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Foreground = new SolidColorBrush(pal.Text),
            VerticalAlignment = VerticalAlignment.Center
        };

        row.Children.Add(_badge);
        row.Children.Add(_label);

        _card = new Border
        {
            Padding = new Thickness(11, 7, 14, 7),
            Background = new LinearGradientBrush(pal.BgTop, pal.BgBottom, 90),
            Child = row
        };

        Content = _card;

        _holdTimer = new DispatcherTimer();
        _holdTimer.Tick += (_, _) => HideToast();
    }

    public void PreWarm()
    {
        W11WindowHelper.MakeNonActivatingFlyout(this, _cfg.Palette, clickThrough: true);
        _hwnd = new WindowInteropHelper(this).Handle;
        Left = -32000;
        Top = -32000;
        Show();
        UpdateLayout();
        Hide();
    }

    public void ShowMessageNearCursor(string text, string kind, int holdMs)
    {
        _holdTimer.Stop();
        var pal = _cfg.Palette;
        var m = W11WindowHelper.GetFlyoutMetrics(_cfg);
        Background = new SolidColorBrush(pal.BgBottom);
        if (_hwnd != IntPtr.Zero)
            W11WindowHelper.ApplyDwmFlyoutAttributes(_hwnd, pal, smallCorners: m.SmallWindowCorners);

        text = System.Text.RegularExpressions.Regex.Replace((text ?? "").Trim(), @"\s+", " ");
        if (text.Length > 90) text = text[..89] + "…";

        string glyph;
        Color accent, badgeBg;
        if (kind == "warn")
        {
            glyph = "\uE7BA";
            accent = pal.IsLight ? Color.FromRgb(0xDC, 0x26, 0x26) : Color.FromRgb(0xF8, 0x71, 0x71);
            badgeBg = pal.IsLight ? Color.FromRgb(0xFE, 0xE2, 0xE2) : Color.FromRgb(0x32, 0x1A, 0x1F);
        }
        else if (kind == "info")
        {
            glyph = "\uE946";
            accent = pal.IsLight ? Color.FromRgb(0x25, 0x63, 0xEB) : Color.FromRgb(0x60, 0xA5, 0xFA);
            badgeBg = pal.IsLight ? Color.FromRgb(0xDB, 0xEA, 0xFE) : Color.FromRgb(0x1A, 0x28, 0x3D);
        }
        else
        {
            glyph = "\uE73E";
            accent = pal.IsLight ? Color.FromRgb(0x16, 0xA3, 0x4A) : Color.FromRgb(0x34, 0xD3, 0x99);
            badgeBg = pal.IsLight ? Color.FromRgb(0xDC, 0xFC, 0xE7) : Color.FromRgb(0x13, 0x2D, 0x24);
        }

        _card.Padding = m.ToastPadding;
        _card.Background = new LinearGradientBrush(pal.BgTop, pal.BgBottom, 90);
        _badge.Width = m.ToastBadgeSize;
        _badge.Height = m.ToastBadgeSize;
        _badge.CornerRadius = new CornerRadius(m.ToastBadgeSize / 2.0);
        _badge.Margin = new Thickness(0, 0, 7, 0);
        _badge.Background = new SolidColorBrush(badgeBg);
        _badge.BorderBrush = new SolidColorBrush(accent);
        _badgeGlyph.FontSize = Math.Max(9.5, m.ToastBadgeSize * 0.5);
        _badgeGlyph.Text = glyph;
        _badgeGlyph.Foreground = new SolidColorBrush(accent);
        _label.FontSize = m.ToastFontSize;
        _label.Foreground = new SolidColorBrush(pal.Text);
        _label.Text = text;

        if (!IsVisible)
            Show();
        UpdateLayout();

        Native.GetCursorPos(out var cur);
        double scale = Native.ScaleAt(cur.X, cur.Y);
        var work = Native.WorkAreaAt(cur.X, cur.Y, out _);
        int physW = (int)Math.Round(ActualWidth * scale);
        int physH = (int)Math.Round(ActualHeight * scale);
        int pad = (int)Math.Round(6 * scale);
        int offset = (int)Math.Round(22 * scale);

        int x = Math.Max(work.Left + pad, Math.Min(cur.X - physW / 2, work.Right - physW - pad));
        int y = (cur.Y - physH - offset < work.Top + pad) ? cur.Y + offset : cur.Y - physH - offset;
        y = Math.Min(y, work.Bottom - physH - pad);

        Native.MoveWindowPhysical(_hwnd, x, y);

        W11WindowHelper.AnimateFlyoutIn(this, _card, _cfg.ToastInMs, W11WindowHelper.ShouldAnimate(_cfg));
        _holdTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(200, holdMs));
        _holdTimer.Start();
    }

    public void HideToast()
    {
        _holdTimer.Stop();
        if (IsVisible)
            Hide();
    }
}

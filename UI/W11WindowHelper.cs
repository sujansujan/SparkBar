using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;

namespace SparkBar;

internal readonly record struct FlyoutMetrics(
    // Toolbar metrics
    double BtnHeight,
    double IconMinWidth,
    double PillMinWidth,
    double IconFontSize,
    double TextFontSize,
    double FavTileSize,
    double FavImgSize,
    double SwatchSize,
    double SepHeight,
    double BtnCornerRadius,
    double TextPadX,
    Thickness CardPadding,
    Thickness BtnMargin,
    bool SmallWindowCorners,
    // Submenu, Card, QR & Toast metrics
    double MenuRowHeight,
    double MenuSingleMinWidth,
    double MenuSingleMaxWidth,
    double MenuTwoColItemWidth,
    double MenuHeaderFontSize,
    double KeycapWidth,
    double KeycapHeight,
    double KeycapFontSize,
    double CardWidth,
    double CardBodyMaxHeight,
    double CardTitleFontSize,
    double CardBodyFontSize,
    double CardSubFontSize,
    double QrImageSize,
    double ToastFontSize,
    double ToastBadgeSize,
    Thickness ToastPadding);

internal static class W11WindowHelper
{
    public static readonly FontFamily TextFont = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily DisplayFont = new("Segoe UI Variable Display, Segoe UI");
    public static readonly FontFamily MonoFont = new("Cascadia Mono, Cascadia Code, Consolas, Segoe UI");

    public static Color GetTuiAccentColor(ThemePalette pal) =>
        pal.IsLight ? Color.FromRgb(0x08, 0x91, 0xB2) : Color.FromRgb(0x22, 0xD3, 0xEE);

    public static Color GetTuiFrameColor(ThemePalette pal) =>
        pal.IsLight ? Color.FromRgb(0x0E, 0x74, 0x90) : Color.FromRgb(0x16, 0x4E, 0x63);

    public static Color GetTuiHighlightColor(ThemePalette pal) =>
        pal.IsLight ? Color.FromRgb(0x02, 0x84, 0xC7) : Color.FromRgb(0x38, 0xBD, 0xF8);

    public static UIElement CreateTuiDashFill(char ch, Brush brush, double fontSize, double rowHeight)
    {
        var tb = new TextBlock
        {
            Text = new string(ch, 160),
            FontFamily = MonoFont,
            FontSize = fontSize,
            LineHeight = rowHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Foreground = brush,
            SnapsToDevicePixels = true
        };
        TextOptions.SetTextFormattingMode(tb, TextFormattingMode.Display);
        var canvas = new Canvas
        {
            Height = rowHeight,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            ClipToBounds = true,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(tb, 0);
        Canvas.SetTop(tb, 0);
        canvas.Children.Add(tb);
        return canvas;
    }

    public static Grid CreateTuiHorizontalDivider(ThemePalette pal, FlyoutMetrics m, char leftCh = '├', char fillCh = '─', char rightCh = '┤')
    {
        double fSz = Math.Max(9.5, m.MenuHeaderFontSize + 1.5);
        double rowH = Math.Ceiling(fSz * 1.25);
        var frameBrush = new SolidColorBrush(GetTuiHighlightColor(pal));

        var g = new Grid { Height = rowH, HorizontalAlignment = HorizontalAlignment.Stretch, SnapsToDevicePixels = true };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        TextBlock MakeEnd(char ch) => new()
        {
            Text = ch.ToString(),
            FontFamily = MonoFont,
            FontSize = fSz,
            LineHeight = rowH,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Foreground = frameBrush,
            VerticalAlignment = VerticalAlignment.Center
        };

        var left = MakeEnd(leftCh);
        var mid = CreateTuiDashFill(fillCh, frameBrush, fSz, rowH);
        var right = MakeEnd(rightCh);
        Grid.SetColumn(left, 0);
        Grid.SetColumn(mid, 1);
        Grid.SetColumn(right, 2);
        g.Children.Add(left);
        g.Children.Add(mid);
        g.Children.Add(right);
        return g;
    }

    public static Grid CreateTuiHeaderBar(
        ConfigService cfg,
        string title,
        ThemePalette pal,
        FlyoutMetrics m,
        UIElement leftBtn = null,
        UIElement rightBadgeOrBtn = null,
        UIElement rightCloseBtn = null)
    {
        double fSz = Math.Max(9.5, m.MenuHeaderFontSize + 1.0);
        double rowH = Math.Max(18, Math.Ceiling(fSz * 1.45));
        var frameBrush = new SolidColorBrush(GetTuiHighlightColor(pal));
        var accentBrush = new SolidColorBrush(GetTuiAccentColor(pal));

        var g = new Grid { MinHeight = rowH, HorizontalAlignment = HorizontalAlignment.Stretch, SnapsToDevicePixels = true };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // ╭─
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // leftBtn
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // ░▒▓ TITLE ▓▒░
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // ───
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // rightBadgeOrBtn
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // rightCloseBtn
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // ─╮

        TextBlock MakeMono(string txt, Brush fg, bool bold = false)
        {
            var tb = new TextBlock
            {
                Text = txt,
                FontFamily = MonoFont,
                FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                FontSize = fSz,
                LineHeight = rowH,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                Foreground = fg,
                VerticalAlignment = VerticalAlignment.Center
            };
            TextOptions.SetTextFormattingMode(tb, TextFormattingMode.Display);
            return tb;
        }

        var c0 = MakeMono("╭─", frameBrush);
        Grid.SetColumn(c0, 0);
        g.Children.Add(c0);

        if (leftBtn != null)
        {
            var spLeft = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            spLeft.Children.Add(leftBtn);
            spLeft.Children.Add(MakeMono("─", frameBrush));
            Grid.SetColumn(spLeft, 1);
            g.Children.Add(spLeft);
        }

        var tag = MakeMono($"░▒▓ {title} ▓▒░ ", accentBrush, bold: true);
        Grid.SetColumn(tag, 2);
        g.Children.Add(tag);

        var fill = CreateTuiDashFill('─', frameBrush, fSz, rowH);
        Grid.SetColumn(fill, 3);
        g.Children.Add(fill);

        if (rightBadgeOrBtn != null)
        {
            var w = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 2, 0) };
            w.Children.Add(rightBadgeOrBtn);
            Grid.SetColumn(w, 4);
            g.Children.Add(w);
        }

        if (rightCloseBtn != null)
        {
            var spRight = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 0) };
            spRight.Children.Add(rightCloseBtn);
            Grid.SetColumn(spRight, 5);
            g.Children.Add(spRight);
        }

        var c6 = MakeMono("╮", frameBrush);
        Grid.SetColumn(c6, 6);
        g.Children.Add(c6);
        return g;
    }

    public static Grid CreateTuiFooterBar(string defaultText, ThemePalette pal, FlyoutMetrics m, out TextBlock tickerTb)
    {
        double fSz = Math.Max(9.0, m.MenuHeaderFontSize + 0.5);
        double rowH = Math.Ceiling(fSz * 1.4);
        var frameBrush = new SolidColorBrush(GetTuiHighlightColor(pal));
        var accentBrush = new SolidColorBrush(GetTuiAccentColor(pal));

        var g = new Grid { Height = rowH, HorizontalAlignment = HorizontalAlignment.Stretch, SnapsToDevicePixels = true };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // ╰─
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // ticker
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // ───
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // ╯

        var c0 = new TextBlock
        {
            Text = "╰─ ",
            FontFamily = MonoFont,
            FontSize = fSz,
            LineHeight = rowH,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Foreground = frameBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(c0, 0);
        g.Children.Add(c0);

        tickerTb = new TextBlock
        {
            Text = defaultText,
            FontFamily = MonoFont,
            FontWeight = FontWeights.SemiBold,
            FontSize = fSz - 0.5,
            LineHeight = rowH,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Foreground = accentBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 260,
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        TextOptions.SetTextFormattingMode(tickerTb, TextFormattingMode.Display);
        Grid.SetColumn(tickerTb, 1);
        g.Children.Add(tickerTb);

        var fill = CreateTuiDashFill('─', frameBrush, fSz, rowH);
        Grid.SetColumn(fill, 2);
        g.Children.Add(fill);

        var c3 = new TextBlock
        {
            Text = "╯",
            FontFamily = MonoFont,
            FontSize = fSz,
            LineHeight = rowH,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Foreground = frameBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(c3, 3);
        g.Children.Add(c3);
        return g;
    }

    public static FlyoutMetrics GetFlyoutMetrics(ConfigService cfg)
    {
        return (cfg?.PopupSize ?? "compact").ToLowerInvariant() switch
        {
            "mini" => new FlyoutMetrics(
                BtnHeight: 20, IconMinWidth: 23, PillMinWidth: 26,
                IconFontSize: 11.5, TextFontSize: 10.5,
                FavTileSize: 16, FavImgSize: 13, SwatchSize: 12, SepHeight: 11,
                BtnCornerRadius: 4.5, TextPadX: 5.5,
                CardPadding: new Thickness(2.5),
                BtnMargin: new Thickness(0.5, 0, 0.5, 0),
                SmallWindowCorners: true,
                MenuRowHeight: 20, MenuSingleMinWidth: 136, MenuSingleMaxWidth: 200, MenuTwoColItemWidth: 138,
                MenuHeaderFontSize: 9, KeycapWidth: 15, KeycapHeight: 13, KeycapFontSize: 8.5,
                CardWidth: 280, CardBodyMaxHeight: 180,
                CardTitleFontSize: 12, CardBodyFontSize: 10.5, CardSubFontSize: 9.5,
                QrImageSize: 144, ToastFontSize: 11, ToastBadgeSize: 18,
                ToastPadding: new Thickness(8, 4, 10, 4)),

            "normal" => new FlyoutMetrics(
                BtnHeight: 29, IconMinWidth: 32, PillMinWidth: 36,
                IconFontSize: 14, TextFontSize: 12,
                FavTileSize: 23, FavImgSize: 17, SwatchSize: 16, SepHeight: 15,
                BtnCornerRadius: 6, TextPadX: 8,
                CardPadding: new Thickness(3.5),
                BtnMargin: new Thickness(1, 0, 1, 0),
                SmallWindowCorners: false,
                MenuRowHeight: 27, MenuSingleMinWidth: 170, MenuSingleMaxWidth: 250, MenuTwoColItemWidth: 164,
                MenuHeaderFontSize: 10, KeycapWidth: 19, KeycapHeight: 16, KeycapFontSize: 9.5,
                CardWidth: 332, CardBodyMaxHeight: 240,
                CardTitleFontSize: 14, CardBodyFontSize: 12.5, CardSubFontSize: 11,
                QrImageSize: 196, ToastFontSize: 12.5, ToastBadgeSize: 22,
                ToastPadding: new Thickness(10, 6, 13, 6)),

            "large" => new FlyoutMetrics(
                BtnHeight: 33, IconMinWidth: 36, PillMinWidth: 40,
                IconFontSize: 15.5, TextFontSize: 13,
                FavTileSize: 26, FavImgSize: 20, SwatchSize: 18, SepHeight: 17,
                BtnCornerRadius: 6.5, TextPadX: 9,
                CardPadding: new Thickness(4),
                BtnMargin: new Thickness(1.5, 0, 1.5, 0),
                SmallWindowCorners: false,
                MenuRowHeight: 30, MenuSingleMinWidth: 190, MenuSingleMaxWidth: 275, MenuTwoColItemWidth: 180,
                MenuHeaderFontSize: 10.5, KeycapWidth: 21, KeycapHeight: 17, KeycapFontSize: 10,
                CardWidth: 368, CardBodyMaxHeight: 270,
                CardTitleFontSize: 15, CardBodyFontSize: 13, CardSubFontSize: 11.5,
                QrImageSize: 224, ToastFontSize: 13, ToastBadgeSize: 24,
                ToastPadding: new Thickness(11, 7, 14, 7)),

            _ => new FlyoutMetrics( // "compact" (default)
                BtnHeight: 24, IconMinWidth: 27, PillMinWidth: 30,
                IconFontSize: 12.5, TextFontSize: 11,
                FavTileSize: 19, FavImgSize: 15, SwatchSize: 14, SepHeight: 13,
                BtnCornerRadius: 5, TextPadX: 6.5,
                CardPadding: new Thickness(3),
                BtnMargin: new Thickness(1, 0, 1, 0),
                SmallWindowCorners: false,
                MenuRowHeight: 23, MenuSingleMinWidth: 152, MenuSingleMaxWidth: 226, MenuTwoColItemWidth: 148,
                MenuHeaderFontSize: 9.5, KeycapWidth: 17, KeycapHeight: 14, KeycapFontSize: 9,
                CardWidth: 304, CardBodyMaxHeight: 205,
                CardTitleFontSize: 13, CardBodyFontSize: 11.5, CardSubFontSize: 10,
                QrImageSize: 168, ToastFontSize: 11.5, ToastBadgeSize: 20,
                ToastPadding: new Thickness(9, 5, 11, 5))
        };
    }

    public static Border CreateTopHighlight(ThemePalette pal, double topNegativeMargin = -3)
    {
        var b = new Border
        {
            Height = 1,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(pal.IsLight ? Colors.White : Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(0, topNegativeMargin, 0, 0),
            SnapsToDevicePixels = true,
            IsHitTestVisible = false
        };
        RenderOptions.SetEdgeMode(b, EdgeMode.Aliased);
        return b;
    }

    public static Border CreateKeycapBadge(string hint, ThemePalette pal, FlyoutMetrics m, Thickness? margin = null)
    {
        return new Border
        {
            MinWidth = m.KeycapWidth,
            Height = m.KeycapHeight,
            Padding = new Thickness(3, 0, 3, 0),
            CornerRadius = new CornerRadius(3.5),
            Background = new SolidColorBrush(pal.KeycapBg),
            BorderBrush = new SolidColorBrush(pal.Separator),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = margin ?? new Thickness(0),
            Child = new TextBlock
            {
                Text = hint,
                FontFamily = TextFont,
                FontWeight = FontWeights.SemiBold,
                FontSize = m.KeycapFontSize,
                Foreground = new SolidColorBrush(pal.KeycapText),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
    }

    public static Border CreateActionPillButton(
        ConfigService cfg,
        string iconGlyph,
        string label,
        string hint,
        bool isPrimary,
        ThemePalette pal,
        FlyoutMetrics m,
        Action onClick)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        if (!string.IsNullOrEmpty(iconGlyph))
        {
            row.Children.Add(new TextBlock
            {
                Text = iconGlyph,
                FontFamily = new FontFamily(cfg.IconFontFamily),
                FontSize = Math.Max(10, m.TextFontSize),
                Foreground = new SolidColorBrush(isPrimary ? pal.AccentText : pal.Text),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0)
            });
        }

        row.Children.Add(new TextBlock
        {
            Text = label,
            FontFamily = cfg.IsNeonTui ? MonoFont : TextFont,
            FontWeight = FontWeights.SemiBold,
            FontSize = m.TextFontSize,
            Foreground = new SolidColorBrush(isPrimary ? pal.AccentText : pal.Text),
            VerticalAlignment = VerticalAlignment.Center
        });

        if (!string.IsNullOrEmpty(hint))
            row.Children.Add(CreateKeycapBadge(hint, pal, m, new Thickness(5, 0, 0, 0)));

        var btn = new Border
        {
            Height = m.BtnHeight + 2,
            Padding = new Thickness(m.TextPadX, 0, m.TextPadX, 0),
            CornerRadius = new CornerRadius(cfg.IsNeonTui ? 2.5 : m.BtnCornerRadius),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 4, 0),
            Cursor = Cursors.Hand,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            Child = row
        };

        var hoverBg = new LinearGradientBrush(pal.HoverTop, pal.HoverBottom, 90);
        var hoverBorder = new SolidColorBrush(pal.HoverBorder.Value);
        var surfaceBg = new SolidColorBrush(pal.Surface);
        var sepBorder = new SolidColorBrush(pal.Separator);

        void ApplyVisual(bool hovered, bool pressed)
        {
            row.RenderTransform = pressed ? new TranslateTransform(0, 1) : Transform.Identity;
            if (isPrimary)
            {
                Color topC = pal.IsLight
                    ? (hovered ? Color.FromRgb(0xDB, 0xEA, 0xFE) : Color.FromRgb(0xEF, 0xF6, 0xFF))
                    : (hovered ? Color.FromRgb(0x1E, 0x3A, 0x5F) : Color.FromRgb(0x17, 0x2B, 0x46));
                Color botC = pal.IsLight
                    ? (hovered ? Color.FromRgb(0xBF, 0xDB, 0xFE) : Color.FromRgb(0xDB, 0xEA, 0xFE))
                    : (hovered ? Color.FromRgb(0x17, 0x2B, 0x46) : Color.FromRgb(0x12, 0x22, 0x38));
                Color rimC = pal.IsLight
                    ? (hovered ? Color.FromRgb(0x25, 0x63, 0xEB) : Color.FromRgb(0x60, 0xA5, 0xFA))
                    : (hovered ? Color.FromRgb(0x60, 0xA5, 0xFA) : Color.FromRgb(0x3B, 0x82, 0xF6));
                btn.Background = new LinearGradientBrush(topC, botC, 90);
                btn.BorderBrush = new SolidColorBrush(rimC);
            }
            else if (pressed)
            {
                btn.Background = surfaceBg;
                btn.BorderBrush = sepBorder;
            }
            else if (hovered)
            {
                btn.Background = hoverBg;
                btn.BorderBrush = hoverBorder;
            }
            else
            {
                btn.Background = new SolidColorBrush(pal.PillFill);
                btn.BorderBrush = new SolidColorBrush(pal.PillBorder);
            }
        }

        ApplyVisual(false, false);
        btn.MouseEnter += (_, _) => ApplyVisual(true, false);
        btn.MouseLeave += (_, _) => ApplyVisual(false, false);
        btn.MouseLeftButtonDown += (_, e) => { e.Handled = true; ApplyVisual(true, true); };
        btn.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            ApplyVisual(btn.IsMouseOver, false);
            onClick?.Invoke();
        };

        return btn;
    }

    public static void PositionFlyoutNearPoint(IntPtr hwnd, Window window, int screenX, int screenY, bool preferAbove = true)
    {
        if (hwnd == IntPtr.Zero || window == null) return;
        double scale = Native.ScaleAt(screenX, screenY);
        var work = Native.WorkAreaAt(screenX, screenY, out _);
        int physW = (int)Math.Round(window.ActualWidth * scale);
        int physH = (int)Math.Round(window.ActualHeight * scale);
        int pad = (int)Math.Round(6 * scale);

        int px = Math.Max(work.Left + pad, Math.Min(screenX - physW / 2, work.Right - physW - pad));
        int py;
        if (preferAbove)
        {
            py = screenY - physH - (int)Math.Round(12 * scale);
            if (py < work.Top + pad)
                py = screenY + (int)Math.Round(18 * scale);
        }
        else
        {
            py = screenY + (int)Math.Round(14 * scale);
            if (py + physH > work.Bottom - pad)
                py = screenY - physH - (int)Math.Round(12 * scale);
        }
        py = Math.Max(work.Top + pad, Math.Min(py, work.Bottom - physH - pad));
        Native.MoveWindowPhysical(hwnd, px, py);
    }

    /// <summary>
    /// Positions a secondary popup (submenu, card, QR) directly in the same screen region
    /// where the primary popup was displayed so zero extra mouse movement is needed.
    /// </summary>
    public static void PositionSecondaryFlyout(IntPtr hwnd, Window window, Native.RECT primaryRect, int fallbackX, int fallbackY)
    {
        if (hwnd == IntPtr.Zero || window == null) return;

        if (primaryRect.Width > 0 && primaryRect.Height > 0)
        {
            Native.GetCursorPos(out var cur);
            int centerX = (cur.X >= primaryRect.Left - 20 && cur.X <= primaryRect.Right + 20)
                ? cur.X
                : primaryRect.Left + primaryRect.Width / 2;

            double scale = Native.ScaleAt(centerX, primaryRect.Top);
            var work = Native.WorkAreaAt(centerX, primaryRect.Top, out _);
            int physW = (int)Math.Round(window.ActualWidth * scale);
            int physH = (int)Math.Round(window.ActualHeight * scale);
            int pad = (int)Math.Round(6 * scale);

            int px = Math.Max(work.Left + pad, Math.Min(centerX - physW / 2, work.Right - physW - pad));
            int py = Math.Max(work.Top + pad, Math.Min(primaryRect.Top, work.Bottom - physH - pad));
            Native.MoveWindowPhysical(hwnd, px, py);
            return;
        }

        PositionFlyoutNearPoint(hwnd, window, fallbackX, fallbackY, preferAbove: true);
    }

    public static Border CreateHeaderIconButton(ConfigService cfg, string iconGlyph, string toolTip, ThemePalette pal, FlyoutMetrics m, Action onClick)
    {
        double sz = Math.Max(19, m.MenuRowHeight - 2);
        var hoverBg = new LinearGradientBrush(pal.HoverTop, pal.HoverBottom, 90);
        var hoverBorder = new SolidColorBrush(pal.HoverBorder.Value);

        var tb = new TextBlock
        {
            Text = iconGlyph,
            FontFamily = new FontFamily(cfg.IconFontFamily),
            FontSize = 9.5,
            Foreground = new SolidColorBrush(pal.SubText),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var b = new Border
        {
            Width = sz,
            Height = sz,
            CornerRadius = new CornerRadius(m.BtnCornerRadius),
            BorderThickness = new Thickness(1),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = toolTip,
            Child = tb
        };
        b.MouseEnter += (_, _) => { b.Background = hoverBg; b.BorderBrush = hoverBorder; tb.Foreground = new SolidColorBrush(pal.Text); };
        b.MouseLeave += (_, _) => { b.Background = Brushes.Transparent; b.BorderBrush = Brushes.Transparent; tb.Foreground = new SolidColorBrush(pal.SubText); };
        b.MouseLeftButtonDown += (_, e) => { e.Handled = true; };
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick?.Invoke(); };
        return b;
    }

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

    public static Grid WrapWithTuiSideRails(UIElement content, ThemePalette pal, double horizontalPad = 5)
    {
        if (content is FrameworkElement fe && fe.Parent is Panel oldParent)
            oldParent.Children.Remove(content);

        var frameBrush = new SolidColorBrush(GetTuiHighlightColor(pal));
        var g = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch, SnapsToDevicePixels = true };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Border MakeRail(Thickness margin)
        {
            var r = new Border
            {
                Width = 1.2,
                Background = frameBrush,
                Margin = margin,
                VerticalAlignment = VerticalAlignment.Stretch,
                SnapsToDevicePixels = true
            };
            RenderOptions.SetEdgeMode(r, EdgeMode.Aliased);
            return r;
        }

        var leftRail = MakeRail(new Thickness(2.1, -1.5, horizontalPad, -1.5));
        var rightRail = MakeRail(new Thickness(horizontalPad, -1.5, 2.1, -1.5));
        Grid.SetColumn(leftRail, 0);
        Grid.SetColumn(content, 1);
        Grid.SetColumn(rightRail, 2);
        g.Children.Add(leftRail);
        g.Children.Add(content);
        g.Children.Add(rightRail);
        return g;
    }

    public static void MakeNonActivatingFlyout(Window w, ThemePalette pal, bool clickThrough = false, bool smallCorners = false, bool neonTui = false)
    {
        var helper = new WindowInteropHelper(w);
        IntPtr hwnd = helper.EnsureHandle();

        int exAdd = Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST;
        if (clickThrough) exAdd |= Native.WS_EX_TRANSPARENT;
        Native.AddExStyle(hwnd, exAdd);

        w.Background = new SolidColorBrush(pal.BgBottom);
        ApplyDwmFlyoutAttributes(hwnd, pal, smallCorners, neonTui);

        var src = HwndSource.FromHwnd(hwnd);
        if (src != null)
        {
            src.CompositionTarget.BackgroundColor = pal.BgBottom;
            src.AddHook(NonActivatingWndProc);
        }
    }

    public static void ApplyDwmFlyoutAttributes(IntPtr hwnd, ThemePalette pal, bool smallCorners = false, bool neonTui = false)
    {
        if (hwnd == IntPtr.Zero || pal == null) return;
        Native.SetDwm(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, pal.IsLight ? 0 : 1);
        Native.SetDwm(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, (smallCorners || neonTui) ? DWMWCP_ROUNDSMALL : DWMWCP_ROUND);
        Color bCol = neonTui ? GetTuiFrameColor(pal) : pal.Border;
        int bgrBorder = (bCol.B << 16) | (bCol.G << 8) | bCol.R;
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

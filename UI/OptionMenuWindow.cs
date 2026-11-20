using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SparkBar;

public sealed class OptionMenuItem
{
    public string Label { get; init; } = "";
    public string Glyph { get; init; } = "";
    public BitmapSource Image { get; init; }
    public bool Accent { get; init; }
    public Action OnClick { get; init; }
}

/// <summary>
/// Pre-warmed, persistent option menu flyout window. Scales uniformly via FlyoutMetrics
/// and automatically packs large menus (e.g. Change Case) into a symmetrical 2-column grid.
/// </summary>
internal sealed class OptionMenuWindow : Window
{
    private readonly ConfigService _cfg;
    private readonly Grid _frameRoot;
    private readonly Border _card;
    private readonly Border _topHighlight;
    private readonly Grid _tuiTopHost;
    private readonly Grid _tuiBottomHost;
    private readonly StackPanel _stack;
    private readonly HoverTipWindow _hoverTip;
    private readonly Dictionary<string, Action> _hintMap = new(StringComparer.OrdinalIgnoreCase);
    private TextBlock _tuiTickerTb;
    private string _tuiDefaultTicker = "✦ Hover";
    private Action<int> _onWheelDelta;
    private bool _isOpen;

    public IntPtr Hwnd { get; private set; }
    public bool IsMenuOpen => _isOpen;

    public bool HasHintKey(string key) => _isOpen && _hintMap.ContainsKey(key);

    public void InvokeHintKey(string key)
    {
        if (!_isOpen || !_hintMap.TryGetValue(key, out var fn)) return;
        HideMenu();
        fn?.Invoke();
    }

    public OptionMenuWindow(ConfigService cfg)
    {
        _cfg = cfg;
        var pal = cfg.Palette;

        W11WindowHelper.ConfigureDwmFlyoutChrome(this, neonTui: cfg.IsNeonTui);

        _stack = new StackPanel
        {
            Orientation = Orientation.Vertical
        };

        _topHighlight = W11WindowHelper.CreateTopHighlight(pal, topNegativeMargin: -4.5);

        var inner = new Grid();
        inner.Children.Add(_topHighlight);
        inner.Children.Add(_stack);

        _card = new Border
        {
            Padding = new Thickness(4.5),
            Background = new LinearGradientBrush(pal.BgTop, pal.BgBottom, 90),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            Child = inner
        };
        _card.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width > 24)
                _tuiBottomHost.MaxWidth = Math.Max(48, e.NewSize.Width - 2);
        };

        _tuiTopHost = new Grid
        {
            Background = null,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Visibility = Visibility.Collapsed
        };
        _tuiBottomHost = new Grid
        {
            Background = null,
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Visibility = Visibility.Collapsed
        };

        _frameRoot = new Grid
        {
            Background = null,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };
        _frameRoot.Children.Add(_card);
        _frameRoot.Children.Add(_tuiTopHost);
        _frameRoot.Children.Add(_tuiBottomHost);

        _frameRoot.PreviewMouseWheel += (_, e) =>
        {
            if (_onWheelDelta != null)
            {
                e.Handled = true;
                _onWheelDelta(e.Delta);
            }
        };

        Content = _frameRoot;
        _hoverTip = new HoverTipWindow(cfg);
    }

    public void PreWarm()
    {
        W11WindowHelper.MakeNonActivatingFlyout(this, _cfg.Palette, neonTui: _cfg.IsNeonTui);
        Hwnd = new WindowInteropHelper(this).Handle;
        Left = -32000;
        Top = -32000;
        Show();
        UpdateLayout();
        Hide();
        _hoverTip.PreWarm();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        try { _hoverTip?.Close(); } catch { }
    }

    private void ShowIconHoverTip(Border btnBorder, string label, string hint)
    {
        if (_cfg.IsNeonTui && _tuiTickerTb != null)
        {
            _tuiTickerTb.Text = string.IsNullOrEmpty(hint) ? $"▸ {label}" : $"▸ {label} [{hint}]";
            return;
        }
        try
        {
            Point centerBottom = btnBorder.PointToScreen(new Point(btnBorder.ActualWidth / 2.0, btnBorder.ActualHeight));
            Point cardBottom   = _card.PointToScreen(new Point(0, _card.ActualHeight));
            Point cardTop      = _card.PointToScreen(new Point(0, 0));
            _hoverTip.ShowAnchored(label, hint, (int)centerBottom.X, (int)cardBottom.Y + 5, (int)cardTop.Y - 5);
        }
        catch { }
    }

    private void ClearIconHoverTip()
    {
        if (_cfg.IsNeonTui && _tuiTickerTb != null)
            _tuiTickerTb.Text = _tuiDefaultTicker;
        else
            _hoverTip.HideTip();
    }

    public void ShowItemsAt(
        IReadOnlyList<OptionMenuItem> items,
        string headerTitle,
        int screenX,
        int screenY,
        Native.RECT primaryRect = default,
        Action onBack = null,
        bool iconOnly = false)
    {
        var pal = _cfg.Palette;
        var m = W11WindowHelper.GetFlyoutMetrics(_cfg);
        bool isTui = _cfg.IsNeonTui;
        _hoverTip.HideTip();
        _onWheelDelta = null;
        _tuiTickerTb = null;
        _tuiBottomHost.MaxWidth = double.PositiveInfinity;

        Background = isTui ? Brushes.Transparent : new SolidColorBrush(pal.BgBottom);
        if (Hwnd != IntPtr.Zero)
        {
            Native.RemoveExStyle(Hwnd, Native.WS_EX_TRANSPARENT);
            W11WindowHelper.ApplyDwmFlyoutAttributes(Hwnd, pal, smallCorners: m.SmallWindowCorners, neonTui: isTui);
        }

        double padX = iconOnly ? m.CardPadding.Left : Math.Round(m.CardPadding.Left * 1.5);
        double padY = iconOnly ? m.CardPadding.Top  : Math.Round(m.CardPadding.Top * 1.5);
        W11WindowHelper.ApplyNeonTuiCardChrome(_card, pal, isTui, new Thickness(padX, padY, padX, padY), legendHalfH: W11WindowHelper.GetTuiLegendHalf(m));
        _topHighlight.Visibility = isTui ? Visibility.Collapsed : Visibility.Visible;
        _topHighlight.Background = new SolidColorBrush(pal.IsLight ? Colors.White : Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        _topHighlight.Margin = new Thickness(0, -padY, 0, 0);

        _stack.Children.Clear();
        _stack.MinWidth = 0;
        _stack.MaxWidth = double.PositiveInfinity;
        _tuiTopHost.Children.Clear();
        _tuiBottomHost.Children.Clear();
        _hintMap.Clear();

        if (iconOnly && items != null && items.Count > 0)
        {
            BuildIconOnlyMenu(items, headerTitle, screenX, screenY, primaryRect, onBack, pal, m, isTui);

            if (!IsVisible)
                Show();
            UpdateLayout();

            W11WindowHelper.PositionSecondaryFlyout(Hwnd, this, primaryRect, screenX, screenY);
            _isOpen = true;

            W11WindowHelper.AnimateFlyoutIn(this, _frameRoot, _cfg.FadeInMs, W11WindowHelper.ShouldAnimate(_cfg));
            return;
        }

        var itemFont = isTui ? W11WindowHelper.MonoFont : W11WindowHelper.TextFont;
        var textBrush = new SolidColorBrush(pal.Text);
        var subBrush = new SolidColorBrush(pal.SubText);
        var sepBrush = new SolidColorBrush(pal.Separator);
        var accentBrush = new SolidColorBrush(isTui ? W11WindowHelper.GetTuiAccentColor(pal) : pal.Accent);
        var accentTextBrush = new SolidColorBrush(isTui ? W11WindowHelper.GetTuiAccentColor(pal) : pal.AccentText);
        var hoverBg = new LinearGradientBrush(pal.HoverTop, pal.HoverBottom, 90);
        var hoverBorder = new SolidColorBrush(isTui ? W11WindowHelper.GetTuiHighlightColor(pal) : pal.HoverBorder.Value);

        bool isClipboardMenu = string.Equals(headerTitle, "CLIPBOARD HISTORY", StringComparison.OrdinalIgnoreCase);
        bool useTwoColGrid = !isClipboardMenu && (_cfg.TwoLayerPopup ? items.Count >= 2 : items.Count > 6);
        double tuiBodyPad = isTui ? 10 : 0;

        _stack.MinWidth = (useTwoColGrid ? (m.MenuTwoColItemWidth * 2) + 4 : m.MenuSingleMinWidth) + tuiBodyPad;
        _stack.MaxWidth = (useTwoColGrid ? (m.MenuTwoColItemWidth * 2) + 8 : (isClipboardMenu ? m.CardWidth : m.MenuSingleMaxWidth)) + tuiBodyPad;

        if (isTui)
        {
            _tuiTopHost.Visibility = Visibility.Visible;
            _tuiBottomHost.Visibility = Visibility.Visible;

            var leftPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (onBack != null)
            {
                var backPill = W11WindowHelper.CreateTuiBorderIconPill(_cfg, "\uE72B", "Back to toolbar (Backspace)", pal, m, () =>
                {
                    HideMenu();
                    onBack();
                });
                backPill.Margin = new Thickness(0, 0, 4, 0);
                leftPanel.Children.Add(backPill);
            }
            string hdr = string.IsNullOrEmpty(headerTitle) ? "MENU" : headerTitle;
            leftPanel.Children.Add(W11WindowHelper.CreateTuiLegendPill(hdr, pal, m, out _, bold: true, isBottom: false));

            var closePill = W11WindowHelper.CreateTuiBorderIconPill(_cfg, "\uE711", "Close (Esc)", pal, m, HideMenu);
            _tuiTopHost.Children.Add(W11WindowHelper.CreateTuiTopLegendOverlay(pal, m, leftPanel, closePill));
        }
        else if (!string.IsNullOrEmpty(headerTitle))
        {
            _tuiTopHost.Visibility = Visibility.Collapsed;
            _tuiBottomHost.Visibility = Visibility.Collapsed;

            Border backBtn = null;
            if (onBack != null)
            {
                backBtn = W11WindowHelper.CreateHeaderIconButton(_cfg, "\uE72B", "Back to toolbar (Backspace)", pal, m, () =>
                {
                    HideMenu();
                    onBack();
                });
            }
            var closeBtn = W11WindowHelper.CreateHeaderIconButton(_cfg, "\uE711", "Close (Esc)", pal, m, HideMenu);

            var hdrGrid = new Grid { Margin = new Thickness(4, 1, 2, 3) };
            hdrGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hdrGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hdrGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            if (backBtn != null)
            {
                backBtn.Margin = new Thickness(0, 0, 4, 0);
                Grid.SetColumn(backBtn, 0);
                hdrGrid.Children.Add(backBtn);
            }

            var hdrText = new TextBlock
            {
                Text = headerTitle,
                FontFamily = W11WindowHelper.TextFont,
                FontWeight = FontWeights.Bold,
                FontSize = m.MenuHeaderFontSize,
                Foreground = subBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(hdrText, 1);
            hdrGrid.Children.Add(hdrText);

            Grid.SetColumn(closeBtn, 2);
            hdrGrid.Children.Add(closeBtn);

            _stack.Children.Add(hdrGrid);
            var hdrDiv = new Border
            {
                Height = 1,
                Background = sepBrush,
                SnapsToDevicePixels = true,
                Margin = new Thickness(4, 0, 4, 3)
            };
            RenderOptions.SetEdgeMode(hdrDiv, EdgeMode.Aliased);
            _stack.Children.Add(hdrDiv);
        }

        Border BuildRowBorder(OptionMenuItem item, string hint, bool isTwoCol)
        {
            double tileColW = isTwoCol ? (m.FavTileSize + 8) : (m.FavTileSize + 10);
            var rowGrid = new Grid
            {
                Height = m.MenuRowHeight,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(tileColW) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var leftAccentBar = new Border
            {
                Width = 2.5,
                Height = Math.Max(10, m.MenuRowHeight * 0.52),
                CornerRadius = new CornerRadius(1.25),
                Background = accentBrush,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(1.5, 0, 0, 0),
                Visibility = Visibility.Hidden
            };
            rowGrid.Children.Add(leftAccentBar);

            if (item.Image != null || !string.IsNullOrEmpty(item.Glyph))
            {
                var tile = new Border
                {
                    Width = m.FavTileSize,
                    Height = m.FavTileSize,
                    CornerRadius = new CornerRadius(isTui ? 2.5 : 4),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(3, 0, 0, 0),
                    BorderThickness = new Thickness(1),
                    SnapsToDevicePixels = true
                };

                if (item.Image != null)
                {
                    tile.Background = new SolidColorBrush(pal.FaviconTile);
                    tile.BorderBrush = sepBrush;
                    var img = new Image
                    {
                        Width = m.FavImgSize,
                        Height = m.FavImgSize,
                        Source = item.Image,
                        Stretch = Stretch.Uniform
                    };
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                    tile.Child = img;
                }
                else
                {
                    tile.Background = new SolidColorBrush(item.Accent ? pal.AccentBgTop : pal.Surface);
                    tile.BorderBrush = item.Accent ? new SolidColorBrush(pal.AccentRim) : sepBrush;
                    tile.Child = new TextBlock
                    {
                        Text = item.Glyph,
                        FontFamily = itemFont,
                        FontWeight = FontWeights.SemiBold,
                        FontSize = Math.Max(8.5, m.TextFontSize - 1.5),
                        Foreground = item.Accent ? accentTextBrush : textBrush,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                }
                Grid.SetColumn(tile, 0);
                rowGrid.Children.Add(tile);
            }

            var lbl = new TextBlock
            {
                Text = item.Label,
                FontFamily = itemFont,
                FontWeight = item.Accent ? FontWeights.SemiBold : FontWeights.Normal,
                FontSize = m.TextFontSize,
                Foreground = item.Accent ? accentTextBrush : textBrush,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(4, 0, 4, 0)
            };
            Grid.SetColumn(lbl, 1);
            rowGrid.Children.Add(lbl);

            var keycap = W11WindowHelper.CreateKeycapBadge(hint, pal, m, new Thickness(0, 0, 4, 0));
            Grid.SetColumn(keycap, 2);
            rowGrid.Children.Add(keycap);

            var rowBorder = new Border
            {
                CornerRadius = new CornerRadius(isTui ? 2.5 : m.BtnCornerRadius),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(isTwoCol ? 1 : 0, 0.5, isTwoCol ? 1 : 0, 0.5),
                Cursor = Cursors.Hand,
                SnapsToDevicePixels = true,
                Child = rowGrid
            };

            rowBorder.MouseEnter += (_, _) =>
            {
                rowBorder.Background = hoverBg;
                rowBorder.BorderBrush = hoverBorder;
                leftAccentBar.Visibility = Visibility.Visible;
            };
            rowBorder.MouseLeave += (_, _) =>
            {
                rowBorder.Background = Brushes.Transparent;
                rowBorder.BorderBrush = Brushes.Transparent;
                leftAccentBar.Visibility = Visibility.Hidden;
            };
            rowBorder.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                HideMenu();
                item.OnClick?.Invoke();
            };

            return rowBorder;
        }

        var bodyHost = new StackPanel { Orientation = Orientation.Vertical };

        if (useTwoColGrid)
        {
            int startIdx = 0;
            int endExclusive = items.Count;
            if (items[0].Accent)
            {
                string h0 = SmartParser.HintForIndex(1);
                if (items[0].OnClick != null) _hintMap[h0] = items[0].OnClick;
                bodyHost.Children.Add(BuildRowBorder(items[0], h0, isTwoCol: false));

                var div = new Border
                {
                    Height = 1,
                    Background = sepBrush,
                    SnapsToDevicePixels = true,
                    Margin = new Thickness(4, 2, 4, 2)
                };
                RenderOptions.SetEdgeMode(div, EdgeMode.Aliased);
                bodyHost.Children.Add(div);
                startIdx = 1;
            }

            bool hasFooterAccent = items.Count - startIdx >= 2 && items[items.Count - 1].Accent;
            if (hasFooterAccent)
                endExclusive = items.Count - 1;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(m.MenuTwoColItemWidth) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(m.MenuTwoColItemWidth) });

            int remaining = endExclusive - startIdx;
            int rows = (remaining + 1) / 2;
            for (int r = 0; r < rows; r++)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            for (int i = startIdx; i < endExclusive; i++)
            {
                var item = items[i];
                string hint = SmartParser.HintForIndex(i + 1);
                if (item.OnClick != null) _hintMap[hint] = item.OnClick;

                int rel = i - startIdx;
                int row = rel / 2;
                int col = rel % 2;

                var rb = BuildRowBorder(item, hint, isTwoCol: true);
                Grid.SetRow(rb, row);
                Grid.SetColumn(rb, col);
                if (rel == remaining - 1 && col == 0)
                    Grid.SetColumnSpan(rb, 2);

                grid.Children.Add(rb);
            }
            bodyHost.Children.Add(grid);

            if (hasFooterAccent)
            {
                var div = new Border
                {
                    Height = 1,
                    Background = sepBrush,
                    SnapsToDevicePixels = true,
                    Margin = new Thickness(4, 2, 4, 2)
                };
                RenderOptions.SetEdgeMode(div, EdgeMode.Aliased);
                bodyHost.Children.Add(div);

                int lastIdx = items.Count - 1;
                string hLast = SmartParser.HintForIndex(lastIdx + 1);
                if (items[lastIdx].OnClick != null) _hintMap[hLast] = items[lastIdx].OnClick;
                bodyHost.Children.Add(BuildRowBorder(items[lastIdx], hLast, isTwoCol: false));
            }
        }
        else
        {
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                int idx = i + 1;
                string hint = SmartParser.HintForIndex(idx);
                if (item.OnClick != null)
                    _hintMap[hint] = item.OnClick;

                bodyHost.Children.Add(BuildRowBorder(item, hint, isTwoCol: false));

                if (idx == 1 && item.Accent && items.Count > 1)
                {
                    var div = new Border
                    {
                        Height = 1,
                        Background = sepBrush,
                        SnapsToDevicePixels = true,
                        Margin = new Thickness(4, 2, 4, 2)
                    };
                    RenderOptions.SetEdgeMode(div, EdgeMode.Aliased);
                    bodyHost.Children.Add(div);
                }
            }
        }

        if (isTui)
        {
            bodyHost.Margin = new Thickness(5, 11, 5, 11);
            _stack.Children.Add(bodyHost);
            _tuiBottomHost.Children.Add(W11WindowHelper.CreateTuiBottomLegendOverlay(
                onBack != null ? "Esc · Backspace: ←" : "Esc: close", pal, m, out _));
        }
        else
        {
            _tuiTopHost.Visibility = Visibility.Collapsed;
            _tuiBottomHost.Visibility = Visibility.Collapsed;
            bodyHost.Margin = new Thickness(0);
            _stack.Children.Add(bodyHost);
        }

        if (!IsVisible)
            Show();
        UpdateLayout();

        W11WindowHelper.PositionSecondaryFlyout(Hwnd, this, primaryRect, screenX, screenY);
        _isOpen = true;

        W11WindowHelper.AnimateFlyoutIn(this, _frameRoot, _cfg.FadeInMs, W11WindowHelper.ShouldAnimate(_cfg));
    }

    public void HideMenu()
    {
        _hoverTip.HideTip();
        _onWheelDelta = null;
        if (!_isOpen && !IsVisible) return;
        _isOpen = false;
        _frameRoot.BeginAnimation(UIElement.OpacityProperty, null);
        Hide();
    }

    private void BuildIconOnlyMenu(
        IReadOnlyList<OptionMenuItem> items,
        string headerTitle,
        int screenX,
        int screenY,
        Native.RECT primaryRect,
        Action onBack,
        ThemePalette pal,
        FlyoutMetrics m,
        bool isTui)
    {
        var itemFont = isTui ? W11WindowHelper.MonoFont : W11WindowHelper.TextFont;
        var textBrush = new SolidColorBrush(pal.Text);
        var subBrush = new SolidColorBrush(pal.SubText);
        var sepBrush = new SolidColorBrush(pal.Separator);
        var accentBarBrush = new SolidColorBrush(pal.Accent);
        var accentTextBrush = new SolidColorBrush(isTui ? W11WindowHelper.GetTuiAccentColor(pal) : pal.AccentText);
        var tuiFrameBrush = new SolidColorBrush(W11WindowHelper.GetTuiHighlightColor(pal));
        var tuiAccentBrush = new SolidColorBrush(W11WindowHelper.GetTuiAccentColor(pal));
        var hoverBg = new LinearGradientBrush(pal.HoverTop, pal.HoverBottom, 90);
        var hoverBorder = new SolidColorBrush(isTui ? W11WindowHelper.GetTuiHighlightColor(pal) : pal.HoverBorder.Value);
        var surfaceBrush = new SolidColorBrush(pal.Surface);

        double scale = Native.ScaleAt(screenX, screenY);
        int visibleRows = _cfg.TwoLayerPopup ? 2 : 1;
        double tileSlotW = m.IconMinWidth + 4;
        double divW = isTui ? 1 : 3; // TUI 1px hairline vs Classic 1px + 1px L/R margin
        double cellStepW = tileSlotW + divW;

        double primaryDipW = primaryRect.Width > 0
            ? (primaryRect.Width / scale)
            : (visibleRows == 2 ? cellStepW * 4 + 6 : cellStepW * 8 + 6);

        double padX = m.CardPadding.Left;
        double classicChromeW = isTui ? 0 : (padX * 2 + (onBack != null ? 26 : 0));
        int maxCols = Math.Max(3, (int)Math.Floor((primaryDipW - (isTui ? 4 : classicChromeW) + 2) / cellStepW));
        int minCols = isTui ? 4 : 3;
        int desiredCols = visibleRows == 2 ? (int)Math.Ceiling(items.Count / 2.0) : items.Count;
        int cols = Math.Max(Math.Min(minCols, Math.Max(3, maxCols)), Math.Min(maxCols, desiredCols));
        bool needsScroll = items.Count > cols * visibleRows;

        if (!isTui && needsScroll)
        {
            double scrollChromeW = visibleRows == 1 ? 36 : 22;
            cols = Math.Max(3, (int)Math.Floor((primaryDipW - classicChromeW - scrollChromeW + 2) / cellStepW));
            needsScroll = items.Count > cols * visibleRows;
        }

        Border BuildIconTile(OptionMenuItem item, string hint, Thickness tileMargin)
        {
            var btnGrid = new Grid
            {
                Width = m.IconMinWidth,
                Height = m.BtnHeight,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };

            if (item.Image != null)
            {
                var tile = new Border
                {
                    Width = m.FavTileSize,
                    Height = m.FavTileSize,
                    CornerRadius = new CornerRadius(isTui ? 3.5 : 4.5),
                    Background = new SolidColorBrush(pal.FaviconTile),
                    BorderBrush = new SolidColorBrush(item.Accent
                        ? (isTui ? W11WindowHelper.GetTuiHighlightColor(pal) : pal.AccentRim)
                        : (pal.IsLight ? Color.FromRgb(0xCB, 0xD5, 0xE1) : Color.FromRgb(0x32, 0x38, 0x4C))),
                    BorderThickness = new Thickness(1),
                    SnapsToDevicePixels = true,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var img = new Image
                {
                    Width = m.FavImgSize,
                    Height = m.FavImgSize,
                    Source = item.Image,
                    Stretch = Stretch.Uniform,
                    SnapsToDevicePixels = true
                };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                tile.Child = img;
                btnGrid.Children.Add(tile);

                if (item.Accent)
                {
                    var star = new TextBlock
                    {
                        Text = "★",
                        FontFamily = W11WindowHelper.MonoFont,
                        FontWeight = FontWeights.Bold,
                        FontSize = 8.0,
                        LineHeight = 8.0,
                        LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                        Foreground = isTui ? tuiAccentBrush : accentTextBrush,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(0, 0.5, 1.5, 0),
                        IsHitTestVisible = false
                    };
                    TextOptions.SetTextFormattingMode(star, TextFormattingMode.Display);
                    btnGrid.Children.Add(star);
                }
            }
            else
            {
                string glyph = !string.IsNullOrEmpty(item.Glyph)
                    ? item.Glyph
                    : (item.Label.Length >= 2 ? item.Label[..2] : item.Label);
                double fsz = glyph.Length <= 1
                    ? Math.Max(12.0, m.IconFontSize - 1.0)
                    : Math.Max(10.0, m.TextFontSize + 0.5);
                var tb = new TextBlock
                {
                    Text = glyph,
                    FontFamily = itemFont,
                    FontWeight = FontWeights.Bold,
                    FontSize = fsz,
                    Foreground = item.Accent ? accentTextBrush : textBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    SnapsToDevicePixels = true
                };
                TextOptions.SetTextFormattingMode(tb, TextFormattingMode.Display);
                btnGrid.Children.Add(tb);
            }

            var bottomBar = new Border
            {
                Width = 10,
                Height = 2,
                CornerRadius = new CornerRadius(1),
                Background = accentBarBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Visibility = Visibility.Hidden
            };
            btnGrid.Children.Add(bottomBar);

            if (_cfg.ShowHintBadges)
            {
                btnGrid.Children.Add(new TextBlock
                {
                    Text = hint,
                    FontFamily = itemFont,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 7.5,
                    Foreground = new SolidColorBrush(Color.FromArgb(195, pal.SubText.R, pal.SubText.G, pal.SubText.B)),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 1.5, 0),
                    IsHitTestVisible = false
                });
            }

            var btnBorder = new Border
            {
                CornerRadius = new CornerRadius(m.BtnCornerRadius),
                BorderThickness = new Thickness(1),
                Margin = tileMargin,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true,
                Cursor = Cursors.Hand,
                Child = btnGrid
            };

            void ApplyVisual(bool hovered, bool pressed)
            {
                bottomBar.Visibility = hovered ? Visibility.Visible : Visibility.Hidden;
                btnGrid.RenderTransform = pressed ? new TranslateTransform(0, 1) : Transform.Identity;
                if (item.Accent)
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
                    btnBorder.Background = new LinearGradientBrush(topC, botC, 90);
                    btnBorder.BorderBrush = new SolidColorBrush(rimC);
                }
                else if (pressed)
                {
                    btnBorder.Background = surfaceBrush;
                    btnBorder.BorderBrush = sepBrush;
                }
                else if (hovered)
                {
                    btnBorder.Background = hoverBg;
                    btnBorder.BorderBrush = hoverBorder;
                }
                else
                {
                    btnBorder.Background = Brushes.Transparent;
                    btnBorder.BorderBrush = Brushes.Transparent;
                }
            }

            ApplyVisual(false, false);
            btnBorder.MouseEnter += (_, _) =>
            {
                ApplyVisual(true, false);
                ShowIconHoverTip(btnBorder, item.Label, hint);
            };
            btnBorder.MouseLeave += (_, _) =>
            {
                ApplyVisual(false, false);
                ClearIconHoverTip();
            };
            btnBorder.MouseLeftButtonDown += (_, e) => { e.Handled = true; ApplyVisual(true, true); };
            btnBorder.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                HideMenu();
                item.OnClick?.Invoke();
            };

            return btnBorder;
        }

        Border MakeClassicMiniBtn(string iconGlyph, string tip, double width, double height, Action onClick)
        {
            var tb = new TextBlock
            {
                Text = iconGlyph,
                FontFamily = new FontFamily(_cfg.IconFontFamily),
                FontSize = Math.Max(9.0, m.MenuHeaderFontSize + 0.5),
                Foreground = subBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var b = new Border
            {
                Width = width,
                Height = height,
                CornerRadius = new CornerRadius(m.BtnCornerRadius),
                BorderThickness = new Thickness(1),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true,
                Child = tb
            };
            b.MouseEnter += (_, _) => { b.Background = hoverBg; b.BorderBrush = hoverBorder; tb.Foreground = textBrush; ShowIconHoverTip(b, tip, ""); };
            b.MouseLeave += (_, _) => { b.Background = Brushes.Transparent; b.BorderBrush = Brushes.Transparent; tb.Foreground = subBrush; ClearIconHoverTip(); };
            b.MouseLeftButtonDown += (_, e) => { e.Handled = true; };
            b.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick?.Invoke(); };
            return b;
        }

        Border MakeVertDivider(bool stretch)
        {
            var d = new Border
            {
                Width = 1,
                Height = stretch ? double.NaN : m.SepHeight,
                Background = isTui ? tuiFrameBrush : sepBrush,
                Opacity = isTui ? (pal.IsLight ? 0.30 : 0.36) : 0.65,
                VerticalAlignment = stretch ? VerticalAlignment.Stretch : VerticalAlignment.Center,
                Margin = stretch ? new Thickness(0) : new Thickness(1, 0, 1, 0),
                SnapsToDevicePixels = true,
                IsHitTestVisible = false
            };
            RenderOptions.SetEdgeMode(d, EdgeMode.Aliased);
            return d;
        }

        UIElement tuiScrollRightControls = null;

        if (visibleRows == 2)
        {
            int totalRows = (items.Count + cols - 1) / cols;
            double tilePadY = isTui ? 3 : 1;
            double rowH = m.BtnHeight + (tilePadY * 2) + 2;
            var rowsPanel = new StackPanel { Orientation = Orientation.Vertical };

            for (int r = 0; r < totalRows; r++)
            {
                if (r > 0)
                {
                    var hDiv = new Border
                    {
                        Height = 1,
                        Background = isTui ? tuiFrameBrush : sepBrush,
                        Opacity = isTui ? (pal.IsLight ? 0.45 : 0.55) : 0.70,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        SnapsToDevicePixels = true,
                        IsHitTestVisible = false
                    };
                    RenderOptions.SetEdgeMode(hDiv, EdgeMode.Aliased);
                    rowsPanel.Children.Add(hDiv);
                }

                var rowGrid = new Grid { Height = rowH, HorizontalAlignment = HorizontalAlignment.Stretch };
                int colIdx = 0;
                for (int c = 0; c < cols; c++)
                {
                    if (c > 0)
                    {
                        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        var vDiv = MakeVertDivider(stretch: isTui);
                        Grid.SetColumn(vDiv, colIdx++);
                        rowGrid.Children.Add(vDiv);
                    }

                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(tileSlotW) });
                    int itemIdx = r * cols + c;
                    if (itemIdx < items.Count)
                    {
                        var item = items[itemIdx];
                        string hint = SmartParser.HintForIndex(itemIdx + 1);
                        if (item.OnClick != null) _hintMap[hint] = item.OnClick;

                        var tile = BuildIconTile(item, hint, new Thickness(1, tilePadY, 1, tilePadY));
                        Grid.SetColumn(tile, colIdx);
                        rowGrid.Children.Add(tile);
                    }
                    colIdx++;
                }
                rowsPanel.Children.Add(rowGrid);
            }

            int shownRows = Math.Min(totalRows, 2);
            double viewportH = shownRows * rowH + (shownRows > 1 ? 1 : 0);
            double rowStep = rowH + 1;

            var sv = new ScrollViewer
            {
                Content = rowsPanel,
                Height = viewportH,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                CanContentScroll = false,
                Margin = isTui ? new Thickness(0, 7, 0, 7) : new Thickness(0),
                SnapsToDevicePixels = true
            };

            if (needsScroll)
            {
                void ScrollVert(int dir)
                {
                    double next = Math.Max(0, Math.Min(sv.ScrollableHeight, sv.VerticalOffset + (dir * rowStep)));
                    sv.ScrollToVerticalOffset(next);
                }

                _onWheelDelta = delta => ScrollVert(delta > 0 ? -1 : 1);

                if (isTui)
                {
                    var upPill = W11WindowHelper.CreateTuiBorderIconPill(_cfg, "\uE70E", "Scroll up", pal, m, () => ScrollVert(-1), isBottom: true);
                    var dnPill = W11WindowHelper.CreateTuiBorderIconPill(_cfg, "\uE70D", "Scroll down", pal, m, () => ScrollVert(1), isBottom: true);
                    upPill.Margin = new Thickness(0, 0, 3, 0);
                    var hlColor = W11WindowHelper.GetTuiHighlightColor(pal);
                    var activeBorder = new SolidColorBrush(hlColor);
                    var dimBorder = new SolidColorBrush(Color.FromArgb(115, hlColor.R, hlColor.G, hlColor.B));

                    void SetPillEnabledVisual(Border pill, bool enabled)
                    {
                        pill.BorderBrush = enabled ? activeBorder : dimBorder;
                        if (pill.Child is UIElement ch) ch.Opacity = enabled ? 1.0 : 0.30;
                    }

                    void RefreshArrows()
                    {
                        SetPillEnabledVisual(upPill, sv.VerticalOffset > 0.5);
                        SetPillEnabledVisual(dnPill, sv.VerticalOffset < sv.ScrollableHeight - 0.5);
                    }
                    sv.ScrollChanged += (_, _) => RefreshArrows();
                    sv.Loaded += (_, _) => RefreshArrows();
                    SetPillEnabledVisual(upPill, false);

                    var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                    sp.Children.Add(upPill);
                    sp.Children.Add(dnPill);
                    tuiScrollRightControls = sp;
                }
                else
                {
                    var upBtn = MakeClassicMiniBtn("\uE70E", "Scroll up", 18, Math.Max(18, (viewportH - 2) / 2.0), () => ScrollVert(-1));
                    var dnBtn = MakeClassicMiniBtn("\uE70D", "Scroll down", 18, Math.Max(18, (viewportH - 2) / 2.0), () => ScrollVert(1));
                    upBtn.Opacity = 0.38;
                    sv.ScrollChanged += (_, _) =>
                    {
                        upBtn.Opacity = sv.VerticalOffset > 0.5 ? 1.0 : 0.38;
                        dnBtn.Opacity = sv.VerticalOffset < sv.ScrollableHeight - 0.5 ? 1.0 : 0.38;
                    };

                    var scrollCol = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
                    scrollCol.Children.Add(upBtn);
                    scrollCol.Children.Add(dnBtn);

                    var outer = new Grid();
                    int cIdx = 0;
                    if (onBack != null)
                    {
                        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        var backBtn = MakeClassicMiniBtn("\uE72B", "Back (Backspace)", 22, viewportH, () => { HideMenu(); onBack(); });
                        Grid.SetColumn(backBtn, cIdx++);
                        outer.Children.Add(backBtn);

                        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        var sepL = MakeVertDivider(stretch: true);
                        sepL.Margin = new Thickness(1, 3, 1, 3);
                        Grid.SetColumn(sepL, cIdx++);
                        outer.Children.Add(sepL);
                    }
                    outer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    Grid.SetColumn(sv, cIdx++);
                    outer.Children.Add(sv);

                    outer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var sepR = MakeVertDivider(stretch: true);
                    sepR.Margin = new Thickness(1, 3, 1, 3);
                    Grid.SetColumn(sepR, cIdx++);
                    outer.Children.Add(sepR);

                    outer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    Grid.SetColumn(scrollCol, cIdx);
                    outer.Children.Add(scrollCol);

                    _stack.Children.Add(outer);
                }
            }

            if (isTui)
            {
                _stack.Children.Add(sv);
            }
            else if (!needsScroll)
            {
                var outer = new Grid();
                int cIdx = 0;
                if (onBack != null)
                {
                    outer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var backBtn = MakeClassicMiniBtn("\uE72B", "Back (Backspace)", 22, viewportH, () => { HideMenu(); onBack(); });
                    Grid.SetColumn(backBtn, cIdx++);
                    outer.Children.Add(backBtn);

                    outer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var sepL = MakeVertDivider(stretch: true);
                    sepL.Margin = new Thickness(1, 3, 1, 3);
                    Grid.SetColumn(sepL, cIdx++);
                    outer.Children.Add(sepL);
                }
                outer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(sv, cIdx);
                outer.Children.Add(sv);
                _stack.Children.Add(outer);
            }
        }
        else
        {
            // Single-line icon strip with horizontal scroll
            double tilePadY = isTui ? 2 : 0;
            double stripH = m.BtnHeight + (tilePadY * 2) + 2;
            var stripGrid = new Grid { Height = stripH };
            int colIdx = 0;
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0)
                {
                    stripGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var vDiv = MakeVertDivider(stretch: isTui);
                    Grid.SetColumn(vDiv, colIdx++);
                    stripGrid.Children.Add(vDiv);
                }
                stripGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(tileSlotW) });
                var item = items[i];
                string hint = SmartParser.HintForIndex(i + 1);
                if (item.OnClick != null) _hintMap[hint] = item.OnClick;
                var tile = BuildIconTile(item, hint, new Thickness(1, tilePadY, 1, tilePadY));
                Grid.SetColumn(tile, colIdx++);
                stripGrid.Children.Add(tile);
            }

            double viewportW = cols * tileSlotW + Math.Max(0, cols - 1) * divW;
            double horizStep = cellStepW * 2;

            var sv = new ScrollViewer
            {
                Content = stripGrid,
                MaxWidth = viewportW,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                CanContentScroll = false,
                Margin = isTui ? new Thickness(0, 8, 0, 9) : new Thickness(0),
                SnapsToDevicePixels = true
            };

            if (needsScroll)
            {
                void ScrollHoriz(int dir)
                {
                    double next = Math.Max(0, Math.Min(sv.ScrollableWidth, sv.HorizontalOffset + (dir * horizStep)));
                    sv.ScrollToHorizontalOffset(next);
                }

                _onWheelDelta = delta => ScrollHoriz(delta > 0 ? -1 : 1);

                if (isTui)
                {
                    var leftPill = W11WindowHelper.CreateTuiBorderIconPill(_cfg, "\uE76B", "Scroll left", pal, m, () => ScrollHoriz(-1), isBottom: true);
                    var rightPill = W11WindowHelper.CreateTuiBorderIconPill(_cfg, "\uE76C", "Scroll right", pal, m, () => ScrollHoriz(1), isBottom: true);
                    leftPill.Margin = new Thickness(0, 0, 3, 0);
                    var hlColor = W11WindowHelper.GetTuiHighlightColor(pal);
                    var activeBorder = new SolidColorBrush(hlColor);
                    var dimBorder = new SolidColorBrush(Color.FromArgb(115, hlColor.R, hlColor.G, hlColor.B));

                    void SetPillEnabledVisual(Border pill, bool enabled)
                    {
                        pill.BorderBrush = enabled ? activeBorder : dimBorder;
                        if (pill.Child is UIElement ch) ch.Opacity = enabled ? 1.0 : 0.30;
                    }

                    void RefreshArrows()
                    {
                        SetPillEnabledVisual(leftPill, sv.HorizontalOffset > 0.5);
                        SetPillEnabledVisual(rightPill, sv.HorizontalOffset < sv.ScrollableWidth - 0.5);
                    }
                    sv.ScrollChanged += (_, _) => RefreshArrows();
                    sv.Loaded += (_, _) => RefreshArrows();
                    SetPillEnabledVisual(leftPill, false);

                    var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                    sp.Children.Add(leftPill);
                    sp.Children.Add(rightPill);
                    tuiScrollRightControls = sp;
                }
                else
                {
                    var leftBtn = MakeClassicMiniBtn("\uE76B", "Scroll left", 16, m.BtnHeight, () => ScrollHoriz(-1));
                    var rightBtn = MakeClassicMiniBtn("\uE76C", "Scroll right", 16, m.BtnHeight, () => ScrollHoriz(1));
                    leftBtn.Opacity = 0.38;
                    sv.ScrollChanged += (_, _) =>
                    {
                        leftBtn.Opacity = sv.HorizontalOffset > 0.5 ? 1.0 : 0.38;
                        rightBtn.Opacity = sv.HorizontalOffset < sv.ScrollableWidth - 0.5 ? 1.0 : 0.38;
                    };

                    var outer = new Grid();
                    int cIdx = 0;
                    if (onBack != null)
                    {
                        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        var backBtn = MakeClassicMiniBtn("\uE72B", "Back (Backspace)", 22, m.BtnHeight, () => { HideMenu(); onBack(); });
                        Grid.SetColumn(backBtn, cIdx++);
                        outer.Children.Add(backBtn);

                        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        var sepL = MakeVertDivider(stretch: false);
                        Grid.SetColumn(sepL, cIdx++);
                        outer.Children.Add(sepL);
                    }
                    outer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    Grid.SetColumn(leftBtn, cIdx++);
                    outer.Children.Add(leftBtn);

                    outer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    Grid.SetColumn(sv, cIdx++);
                    outer.Children.Add(sv);

                    outer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    Grid.SetColumn(rightBtn, cIdx);
                    outer.Children.Add(rightBtn);

                    _stack.Children.Add(outer);
                }
            }

            if (isTui)
            {
                _stack.Children.Add(sv);
            }
            else if (!needsScroll)
            {
                var outer = new Grid();
                int cIdx = 0;
                if (onBack != null)
                {
                    outer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var backBtn = MakeClassicMiniBtn("\uE72B", "Back (Backspace)", 22, m.BtnHeight, () => { HideMenu(); onBack(); });
                    Grid.SetColumn(backBtn, cIdx++);
                    outer.Children.Add(backBtn);

                    outer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var sepL = MakeVertDivider(stretch: false);
                    Grid.SetColumn(sepL, cIdx++);
                    outer.Children.Add(sepL);
                }
                outer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(sv, cIdx);
                outer.Children.Add(sv);
                _stack.Children.Add(outer);
            }
        }

        if (isTui)
        {
            _tuiTopHost.Visibility = Visibility.Visible;
            _tuiBottomHost.Visibility = Visibility.Visible;

            var leftPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (onBack != null)
            {
                var backPill = W11WindowHelper.CreateTuiBorderIconPill(_cfg, "\uE72B", "Back (Backspace)", pal, m, () =>
                {
                    HideMenu();
                    onBack();
                });
                backPill.Margin = new Thickness(0, 0, 4, 0);
                leftPanel.Children.Add(backPill);
            }
            string hdr = string.IsNullOrEmpty(headerTitle) ? "MENU" : headerTitle;
            leftPanel.Children.Add(W11WindowHelper.CreateTuiLegendPill(hdr, pal, m, out _, bold: true, isBottom: false));

            var closePill = W11WindowHelper.CreateTuiBorderIconPill(_cfg, "\uE711", "Close (Esc)", pal, m, HideMenu);
            _tuiTopHost.Children.Add(W11WindowHelper.CreateTuiTopLegendOverlay(pal, m, leftPanel, closePill));

            _tuiDefaultTicker = needsScroll ? "✦ Scroll" : "✦ Hover";
            _tuiBottomHost.Children.Add(W11WindowHelper.CreateTuiBottomLegendBar(
                _tuiDefaultTicker, pal, m, out _tuiTickerTb, rightContent: tuiScrollRightControls));
        }
        else
        {
            _tuiTopHost.Visibility = Visibility.Collapsed;
            _tuiBottomHost.Visibility = Visibility.Collapsed;
        }
    }
}

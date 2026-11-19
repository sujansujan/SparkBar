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
    private readonly Border _card;
    private readonly Border _topHighlight;
    private readonly StackPanel _stack;
    private readonly Dictionary<string, Action> _hintMap = new(StringComparer.OrdinalIgnoreCase);
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

        W11WindowHelper.ConfigureDwmFlyoutChrome(this);

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

        Content = _card;
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
    }

    public void ShowItemsAt(
        IReadOnlyList<OptionMenuItem> items,
        string headerTitle,
        int screenX,
        int screenY,
        Native.RECT primaryRect = default,
        Action onBack = null)
    {
        var pal = _cfg.Palette;
        var m = W11WindowHelper.GetFlyoutMetrics(_cfg);
        bool isTui = _cfg.IsNeonTui;

        Background = new SolidColorBrush(pal.BgBottom);
        if (Hwnd != IntPtr.Zero)
        {
            Native.RemoveExStyle(Hwnd, Native.WS_EX_TRANSPARENT);
            W11WindowHelper.ApplyDwmFlyoutAttributes(Hwnd, pal, smallCorners: m.SmallWindowCorners, neonTui: isTui);
        }

        double padX = Math.Round(m.CardPadding.Left * 1.5);
        double padY = Math.Round(m.CardPadding.Top * 1.5);
        W11WindowHelper.ApplyNeonTuiCardChrome(_card, pal, isTui, new Thickness(padX, padY, padX, padY));
        _topHighlight.Visibility = isTui ? Visibility.Collapsed : Visibility.Visible;
        _topHighlight.Background = new SolidColorBrush(pal.IsLight ? Colors.White : Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        _topHighlight.Margin = new Thickness(0, -padY, 0, 0);

        _stack.Children.Clear();
        _hintMap.Clear();

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
        double tuiBodyPad = isTui ? 8 : 0;

        _stack.MinWidth = (useTwoColGrid ? (m.MenuTwoColItemWidth * 2) + 4 : m.MenuSingleMinWidth) + tuiBodyPad;
        _stack.MaxWidth = (useTwoColGrid ? (m.MenuTwoColItemWidth * 2) + 8 : (isClipboardMenu ? m.CardWidth : m.MenuSingleMaxWidth)) + tuiBodyPad;

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

        if (isTui)
        {
            string hdr = string.IsNullOrEmpty(headerTitle) ? "MENU" : headerTitle;
            _stack.Children.Add(W11WindowHelper.CreateTuiHeaderBar(_cfg, hdr, pal, m, leftBtn: backBtn, rightCloseBtn: closeBtn));
        }
        else if (!string.IsNullOrEmpty(headerTitle))
        {
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
            bodyHost.Margin = new Thickness(4, 2, 4, 2);
            _stack.Children.Add(bodyHost);
            _stack.Children.Add(W11WindowHelper.CreateTuiFooterBar(onBack != null ? "Esc · Backspace: ←" : "Esc: close", pal, m, out _));
        }
        else
        {
            _stack.Children.Add(bodyHost);
        }

        if (!IsVisible)
            Show();
        UpdateLayout();

        W11WindowHelper.PositionSecondaryFlyout(Hwnd, this, primaryRect, screenX, screenY);
        _isOpen = true;

        W11WindowHelper.AnimateFlyoutIn(this, _card, _cfg.FadeInMs, W11WindowHelper.ShouldAnimate(_cfg));
    }

    public void HideMenu()
    {
        if (!_isOpen && !IsVisible) return;
        _isOpen = false;
        _card.BeginAnimation(UIElement.OpacityProperty, null);
        Hide();
    }
}

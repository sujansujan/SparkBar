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
/// Pre-warmed, persistent option menu flyout window. Reuses its HWND &amp; DirectX surface for &lt; 2ms display.
/// </summary>
internal sealed class OptionMenuWindow : Window
{
    private readonly ConfigService _cfg;
    private readonly Border _card;
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
            Orientation = Orientation.Vertical,
            Width = 268
        };

        _card = new Border
        {
            Padding = new Thickness(6),
            Background = new LinearGradientBrush(pal.BgTop, pal.BgBottom, 90),
            Child = _stack
        };

        Content = _card;
    }

    public void PreWarm()
    {
        W11WindowHelper.MakeNonActivatingFlyout(this, _cfg.Palette);
        Hwnd = new WindowInteropHelper(this).Handle;
        Left = -32000;
        Top = -32000;
        Show();
        UpdateLayout();
        Hide();
    }

    public void ShowItemsAt(IReadOnlyList<OptionMenuItem> items, string headerTitle, int screenX, int screenY)
    {
        var pal = _cfg.Palette;
        if (Hwnd != IntPtr.Zero)
        {
            Native.RemoveExStyle(Hwnd, Native.WS_EX_TRANSPARENT);
            W11WindowHelper.ApplyDwmFlyoutAttributes(Hwnd, pal);
        }

        _card.Background = new LinearGradientBrush(pal.BgTop, pal.BgBottom, 90);
        _stack.Children.Clear();
        _hintMap.Clear();

        var textFont = new FontFamily("Segoe UI Variable Text, Segoe UI");
        var textBrush = new SolidColorBrush(pal.Text);
        var subBrush = new SolidColorBrush(pal.SubText);
        var sepBrush = new SolidColorBrush(pal.Separator);
        var accentBrush = new SolidColorBrush(pal.Accent);
        var accentTextBrush = new SolidColorBrush(pal.AccentText);
        var keycapBgBrush = new SolidColorBrush(pal.KeycapBg);
        var keycapTextBrush = new SolidColorBrush(pal.KeycapText);
        var hoverBg = new LinearGradientBrush(pal.HoverTop, pal.HoverBottom, 90);
        var hoverBorder = new SolidColorBrush(pal.HoverBorder.Value);

        if (!string.IsNullOrEmpty(headerTitle))
        {
            _stack.Children.Add(new TextBlock
            {
                Text = headerTitle,
                FontFamily = textFont,
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                Foreground = subBrush,
                Margin = new Thickness(10, 2, 10, 5)
            });
            _stack.Children.Add(new Border
            {
                Height = 1,
                Background = sepBrush,
                Margin = new Thickness(8, 0, 8, 4)
            });
        }

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            int idx = i + 1;
            string hint = SmartParser.HintForIndex(idx);
            if (item.OnClick != null)
                _hintMap[hint] = item.OnClick;

            bool divBelow = (idx == 1 && item.Accent);

            var rowGrid = new Grid { Height = 32 };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var leftAccentBar = new Border
            {
                Width = 3,
                Height = 14,
                CornerRadius = new CornerRadius(1.5),
                Background = accentBrush,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 0, 0),
                Visibility = Visibility.Hidden
            };
            rowGrid.Children.Add(leftAccentBar);

            if (item.Image != null || !string.IsNullOrEmpty(item.Glyph))
            {
                var tile = new Border
                {
                    Width = 24,
                    Height = 24,
                    CornerRadius = new CornerRadius(5),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 0, 0),
                    BorderThickness = new Thickness(1)
                };

                if (item.Image != null)
                {
                    tile.Background = new SolidColorBrush(pal.FaviconTile);
                    tile.BorderBrush = sepBrush;
                    var img = new Image
                    {
                        Width = 18,
                        Height = 18,
                        Source = item.Image,
                        Stretch = Stretch.Uniform
                    };
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                    tile.Child = img;
                }
                else
                {
                    tile.Background = new SolidColorBrush(item.Accent
                        ? (pal.IsLight ? Color.FromRgb(0xDB, 0xEA, 0xFE) : Color.FromRgb(0x17, 0x2B, 0x46))
                        : pal.Surface);
                    tile.BorderBrush = item.Accent ? accentBrush : sepBrush;
                    tile.Child = new TextBlock
                    {
                        Text = item.Glyph,
                        FontFamily = textFont,
                        FontWeight = FontWeights.SemiBold,
                        FontSize = 11,
                        Foreground = item.Accent ? accentBrush : textBrush,
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
                FontFamily = textFont,
                FontWeight = item.Accent ? FontWeights.SemiBold : FontWeights.Normal,
                FontSize = 13,
                Foreground = item.Accent ? accentTextBrush : textBrush,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(5, 0, 6, 0)
            };
            Grid.SetColumn(lbl, 1);
            rowGrid.Children.Add(lbl);

            var keycap = new Border
            {
                MinWidth = 22,
                Height = 18,
                Padding = new Thickness(4, 0, 4, 0),
                CornerRadius = new CornerRadius(4),
                Background = keycapBgBrush,
                BorderBrush = sepBrush,
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 7, 0),
                Child = new TextBlock
                {
                    Text = hint,
                    FontFamily = textFont,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 10,
                    Foreground = keycapTextBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            Grid.SetColumn(keycap, 2);
            rowGrid.Children.Add(keycap);

            var rowBorder = new Border
            {
                CornerRadius = new CornerRadius(6),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 1, 0, 1),
                Cursor = Cursors.Hand,
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

            _stack.Children.Add(rowBorder);

            if (divBelow)
            {
                _stack.Children.Add(new Border
                {
                    Height = 1,
                    Background = sepBrush,
                    Margin = new Thickness(8, 3, 8, 3)
                });
            }
        }

        if (!IsVisible)
            Show();
        UpdateLayout();

        double scale = Native.ScaleAt(screenX, screenY);
        var work = Native.WorkAreaAt(screenX, screenY, out _);
        int physW = (int)Math.Round(ActualWidth * scale);
        int physH = (int)Math.Round(ActualHeight * scale);
        int pad = (int)Math.Round(6 * scale);

        int px = Math.Max(work.Left + pad, Math.Min(screenX - physW / 4, work.Right - physW - pad));
        int py = Math.Max(work.Top + pad, Math.Min(screenY + (int)Math.Round(18 * scale), work.Bottom - physH - pad));

        Native.MoveWindowPhysical(Hwnd, px, py);
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

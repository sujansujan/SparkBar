using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace SparkBar;

internal sealed class QrWindow : Window
{
    private readonly Grid _frameRoot;
    private readonly Border _card;
    private readonly ConfigService _cfg;
    private readonly Dictionary<string, Action> _hintMap = new(StringComparer.OrdinalIgnoreCase);
    private bool _closing;

    public IntPtr Hwnd => new WindowInteropHelper(this).Handle;

    public bool HasHintKey(string key) => !_closing && _hintMap.ContainsKey(key);

    public void InvokeHintKey(string key)
    {
        if (!_closing && _hintMap.TryGetValue(key, out var fn))
            fn?.Invoke();
    }

    public QrWindow(ConfigService cfg, BitmapSource qrBitmap, string sourceText, Action<string> onToast, Action onBackToToolbar = null)
    {
        _cfg = cfg;
        var pal = cfg.Palette;
        var m = W11WindowHelper.GetFlyoutMetrics(cfg);
        bool isTui = cfg.IsNeonTui;

        W11WindowHelper.ConfigureDwmFlyoutChrome(this, neonTui: isTui);
        Background = isTui ? Brushes.Transparent : new SolidColorBrush(pal.BgBottom);

        var stack = new StackPanel { Orientation = Orientation.Vertical };

        Border charBadge;
        if (isTui)
        {
            charBadge = W11WindowHelper.CreateTuiLegendPill($"{sourceText.Length}ch", pal, m, out _, bold: false, isBottom: false);
            charBadge.Margin = new Thickness(0, 0, 4, 0);
        }
        else
        {
            Border backBtn = null;
            if (onBackToToolbar != null)
            {
                backBtn = W11WindowHelper.CreateHeaderIconButton(cfg, "\uE72B", "Back to toolbar (Backspace)", pal, m, () =>
                {
                    Dismiss();
                    onBackToToolbar();
                });
            }
            charBadge = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 1, 5, 1),
                Background = new SolidColorBrush(pal.PillFill),
                BorderBrush = new SolidColorBrush(pal.PillBorder),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(4, 0, 2, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = $"{sourceText.Length}ch",
                    FontFamily = W11WindowHelper.TextFont,
                    FontSize = m.CardSubFontSize - 0.5,
                    Foreground = new SolidColorBrush(pal.SubText)
                }
            };
            var closeBtn = W11WindowHelper.CreateHeaderIconButton(cfg, "\uE711", "Close (Esc)", pal, m, () => Dismiss());

            var header = new Grid { Margin = new Thickness(0, 0, 0, 7) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            if (backBtn != null)
            {
                backBtn.Margin = new Thickness(0, 0, 5, 0);
                Grid.SetColumn(backBtn, 0);
                header.Children.Add(backBtn);
            }

            var title = new TextBlock
            {
                Text = "QR CODE",
                FontFamily = W11WindowHelper.TextFont,
                FontWeight = FontWeights.Bold,
                FontSize = m.MenuHeaderFontSize,
                Foreground = new SolidColorBrush(pal.SubText),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(title, 1);
            Grid.SetColumn(charBadge, 2);
            Grid.SetColumn(closeBtn, 3);
            header.Children.Add(title);
            header.Children.Add(charBadge);
            header.Children.Add(closeBtn);
            stack.Children.Add(header);
        }

        var imgFrame = new Border
        {
            CornerRadius = new CornerRadius(isTui ? 4 : 8),
            Background = Brushes.White,
            Padding = new Thickness(7),
            Margin = new Thickness(0, isTui ? 4 : 0, 0, 0),
            BorderBrush = new SolidColorBrush(isTui ? W11WindowHelper.GetTuiHighlightColor(pal) : pal.Separator),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new Image
            {
                Width = m.QrImageSize,
                Height = m.QrImageSize,
                Source = qrBitmap,
                Stretch = Stretch.Uniform
            }
        };
        RenderOptions.SetBitmapScalingMode(imgFrame, BitmapScalingMode.NearestNeighbor);

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 7, 0, isTui ? 4 : 0)
        };

        void CopyQr()
        {
            try { Clipboard.SetImage(qrBitmap); onToast?.Invoke("QR code image copied"); } catch { }
            Dismiss();
        }

        void SaveQr()
        {
            var dlg = new SaveFileDialog
            {
                Filter = "PNG Image (*.png)|*.png",
                FileName = "SparkBar_QR.png"
            };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(qrBitmap));
                    using var fs = File.Create(dlg.FileName);
                    enc.Save(fs);
                    onToast?.Invoke("Saved QR PNG");
                }
                catch { }
            }
            Dismiss();
        }

        void CopyAsciiQr()
        {
            string ascii = SmartParser.GenerateAsciiQr(sourceText);
            if (!string.IsNullOrEmpty(ascii))
            {
                UiaHelper.SetClipboardUnicodeText(ascii);
                onToast?.Invoke("Half-block ASCII QR copied");
            }
            Dismiss();
        }

        _hintMap["1"] = CopyQr;
        _hintMap["2"] = SaveQr;
        _hintMap["3"] = CopyAsciiQr;

        btnRow.Children.Add(W11WindowHelper.CreateActionPillButton(cfg, "\uE8C8", "PNG", "1", isPrimary: true, pal, m, CopyQr));
        btnRow.Children.Add(W11WindowHelper.CreateActionPillButton(cfg, "\uE74E", "Save", "2", isPrimary: false, pal, m, SaveQr));
        btnRow.Children.Add(W11WindowHelper.CreateActionPillButton(cfg, null, "▀▄ ASCII", "3", isPrimary: false, pal, m, CopyAsciiQr));

        if (isTui)
        {
            var bodyContent = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(10, 14, 10, 12)
            };
            bodyContent.Children.Add(imgFrame);
            bodyContent.Children.Add(btnRow);
            stack.Children.Add(bodyContent);
        }
        else
        {
            stack.Children.Add(imgFrame);
            stack.Children.Add(btnRow);
        }

        double padX = Math.Round(m.CardPadding.Left * 3.0);
        double padY = Math.Round(m.CardPadding.Top * 2.5);
        var topHighlight = W11WindowHelper.CreateTopHighlight(pal, topNegativeMargin: -padY);
        topHighlight.Visibility = isTui ? Visibility.Collapsed : Visibility.Visible;

        var inner = new Grid();
        inner.Children.Add(topHighlight);
        inner.Children.Add(stack);

        _card = new Border
        {
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            Child = inner
        };
        W11WindowHelper.ApplyNeonTuiCardChrome(_card, pal, isTui, new Thickness(padX, padY, padX, padY), legendHalfH: W11WindowHelper.GetTuiLegendHalf(m));

        _frameRoot = new Grid
        {
            Background = null,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };
        _frameRoot.Children.Add(_card);

        if (isTui)
        {
            var leftPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (onBackToToolbar != null)
            {
                var backPill = W11WindowHelper.CreateTuiBorderIconPill(cfg, "\uE72B", "Back to toolbar (Backspace)", pal, m, () =>
                {
                    Dismiss();
                    onBackToToolbar();
                });
                backPill.Margin = new Thickness(0, 0, 4, 0);
                leftPanel.Children.Add(backPill);
            }
            leftPanel.Children.Add(W11WindowHelper.CreateTuiLegendPill("QR CODE", pal, m, out _, bold: true, isBottom: false));

            var rightPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            rightPanel.Children.Add(charBadge);
            rightPanel.Children.Add(W11WindowHelper.CreateTuiBorderIconPill(cfg, "\uE711", "Close (Esc)", pal, m, () => Dismiss()));

            _frameRoot.Children.Add(W11WindowHelper.CreateTuiTopLegendOverlay(pal, m, leftPanel, rightPanel));
            _frameRoot.Children.Add(W11WindowHelper.CreateTuiBottomLegendOverlay(
                onBackToToolbar != null ? "Esc · Backspace: ←" : "Esc: close", pal, m, out _));
        }

        Content = _frameRoot;
    }

    public void ShowAt(int screenX, int screenY, Native.RECT primaryRect = default)
    {
        var m = W11WindowHelper.GetFlyoutMetrics(_cfg);
        W11WindowHelper.MakeNonActivatingFlyout(this, _cfg.Palette, smallCorners: m.SmallWindowCorners, neonTui: _cfg.IsNeonTui);
        Show();
        UpdateLayout();

        W11WindowHelper.PositionSecondaryFlyout(Hwnd, this, primaryRect, screenX, screenY);
        W11WindowHelper.AnimateFlyoutIn(this, _frameRoot, _cfg.FadeInMs, W11WindowHelper.ShouldAnimate(_cfg));
    }

    public void Dismiss(bool instant = false)
    {
        if (_closing) return;
        _closing = true;
        W11WindowHelper.AnimateFlyoutOutAndClose(this, _frameRoot, _cfg.FadeOutMs, !instant && W11WindowHelper.ShouldAnimate(_cfg));
    }
}

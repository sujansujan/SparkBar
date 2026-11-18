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

    public QrWindow(ConfigService cfg, BitmapSource qrBitmap, string sourceText, Action<string> onToast)
    {
        _cfg = cfg;
        var pal = cfg.Palette;
        var m = W11WindowHelper.GetFlyoutMetrics(cfg);

        W11WindowHelper.ConfigureDwmFlyoutChrome(this);

        var stack = new StackPanel { Orientation = Orientation.Vertical };

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 7) };
        var title = new TextBlock
        {
            Text = "QR CODE",
            FontFamily = W11WindowHelper.TextFont,
            FontWeight = FontWeights.Bold,
            FontSize = m.MenuHeaderFontSize,
            Foreground = new SolidColorBrush(pal.SubText),
            VerticalAlignment = VerticalAlignment.Center
        };
        var charBadge = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 1, 5, 1),
            Background = new SolidColorBrush(pal.PillFill),
            BorderBrush = new SolidColorBrush(pal.PillBorder),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = $"{sourceText.Length} chars",
                FontFamily = W11WindowHelper.TextFont,
                FontSize = m.CardSubFontSize - 0.5,
                Foreground = new SolidColorBrush(pal.SubText)
            }
        };
        DockPanel.SetDock(title, Dock.Left);
        DockPanel.SetDock(charBadge, Dock.Right);
        header.Children.Add(title);
        header.Children.Add(charBadge);
        stack.Children.Add(header);

        var imgFrame = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = Brushes.White,
            Padding = new Thickness(8),
            BorderBrush = new SolidColorBrush(pal.Separator),
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
        stack.Children.Add(imgFrame);

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0)
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

        _hintMap["1"] = CopyQr;
        _hintMap["2"] = SaveQr;

        btnRow.Children.Add(W11WindowHelper.CreateActionPillButton(cfg, "\uE8C8", "Copy QR", "1", isPrimary: true, pal, m, CopyQr));
        btnRow.Children.Add(W11WindowHelper.CreateActionPillButton(cfg, "\uE74E", "Save PNG", "2", isPrimary: false, pal, m, SaveQr));
        stack.Children.Add(btnRow);

        double padX = Math.Round(m.CardPadding.Left * 3.0);
        double padY = Math.Round(m.CardPadding.Top * 2.5);
        var topHighlight = W11WindowHelper.CreateTopHighlight(pal, topNegativeMargin: -padY);

        var inner = new Grid();
        inner.Children.Add(topHighlight);
        inner.Children.Add(stack);

        _card = new Border
        {
            Padding = new Thickness(padX, padY, padX, padY),
            Background = new LinearGradientBrush(pal.BgTop, pal.BgBottom, 90),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            Child = inner
        };

        Content = _card;
    }

    public void ShowAt(int screenX, int screenY)
    {
        var m = W11WindowHelper.GetFlyoutMetrics(_cfg);
        W11WindowHelper.MakeNonActivatingFlyout(this, _cfg.Palette, smallCorners: m.SmallWindowCorners);
        Show();
        UpdateLayout();

        W11WindowHelper.PositionFlyoutNearPoint(Hwnd, this, screenX, screenY, preferAbove: false);
        W11WindowHelper.AnimateFlyoutIn(this, _card, _cfg.FadeInMs, W11WindowHelper.ShouldAnimate(_cfg));
    }

    public void Dismiss(bool instant = false)
    {
        if (_closing) return;
        _closing = true;
        W11WindowHelper.AnimateFlyoutOutAndClose(this, _card, _cfg.FadeOutMs, !instant && W11WindowHelper.ShouldAnimate(_cfg));
    }
}

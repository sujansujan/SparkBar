using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace SparkBar;

internal sealed class QrWindow : Window
{
    private readonly Border _card;
    private readonly ConfigService _cfg;
    private bool _closing;

    public IntPtr Hwnd => new WindowInteropHelper(this).Handle;

    public QrWindow(ConfigService cfg, BitmapSource qrBitmap, string sourceText, Action<string> onToast)
    {
        _cfg = cfg;
        var pal = cfg.Palette;

        W11WindowHelper.ConfigureDwmFlyoutChrome(this);

        var stack = new StackPanel { Orientation = Orientation.Vertical };

        var header = new DockPanel { Margin = new Thickness(2, 0, 2, 10) };
        var title = new TextBlock
        {
            Text = "QR Code",
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            Foreground = new SolidColorBrush(pal.Text),
            VerticalAlignment = VerticalAlignment.Center
        };
        var sub = new TextBlock
        {
            Text = $"{sourceText.Length} chars",
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 11.5,
            Foreground = new SolidColorBrush(pal.SubText),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(title, Dock.Left);
        DockPanel.SetDock(sub, Dock.Right);
        header.Children.Add(title);
        header.Children.Add(sub);
        stack.Children.Add(header);

        var imgFrame = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = Brushes.White,
            Padding = new Thickness(10),
            BorderBrush = new SolidColorBrush(pal.Border),
            BorderThickness = new Thickness(1),
            Child = new Image
            {
                Width = 260,
                Height = 260,
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
            Margin = new Thickness(0, 12, 0, 2)
        };

        Button MakeBtn(string icon, string labelText, Action onClick)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock
            {
                Text = icon,
                FontFamily = new FontFamily(cfg.IconFontFamily),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });
            sp.Children.Add(new TextBlock
            {
                Text = labelText,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
            var b = new Button
            {
                Content = sp,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(4, 0, 4, 0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            b.Click += (_, _) => onClick();
            return b;
        }

        btnRow.Children.Add(MakeBtn("\uE8C8", "Copy Image", () =>
        {
            try { Clipboard.SetImage(qrBitmap); onToast?.Invoke("QR code image copied"); } catch { }
        }));

        btnRow.Children.Add(MakeBtn("\uE74E", "Save PNG...", () =>
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
        }));

        stack.Children.Add(btnRow);

        stack.Children.Add(new TextBlock
        {
            Text = "Press Esc or click outside to close",
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 11.5,
            Foreground = new SolidColorBrush(pal.SubText),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0)
        });

        _card = new Border
        {
            Padding = new Thickness(16, 14, 16, 14),
            Background = new LinearGradientBrush(pal.BgTop, pal.BgBottom, 90),
            Child = stack
        };

        Content = _card;
    }

    public void ShowAt(int screenX, int screenY)
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
        int py = Math.Max(work.Top + pad, Math.Min(screenY - physH / 2, work.Bottom - physH - pad));
        Native.MoveWindowPhysical(Hwnd, px, py);

        W11WindowHelper.AnimateFlyoutIn(this, _card, _cfg.FadeInMs, W11WindowHelper.ShouldAnimate(_cfg));
    }

    public void Dismiss(bool instant = false)
    {
        if (_closing) return;
        _closing = true;
        W11WindowHelper.AnimateFlyoutOutAndClose(this, _card, _cfg.FadeOutMs, !instant && W11WindowHelper.ShouldAnimate(_cfg));
    }
}

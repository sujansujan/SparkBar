using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SparkBar;

public sealed class PopupActionItem
{
    public string Tip { get; init; } = "";
    public string ShortLabel { get; init; } = "";
    public string Glyph { get; init; } = "";
    public bool IsIconGlyph { get; init; }
    public BitmapSource Image { get; init; }
    public Color? SwatchColor { get; init; }
    public bool ForceText { get; init; }
    public bool Accent { get; init; }
    public int Group { get; init; }
    public Action OnClick { get; init; }
    public Action OnRightClick { get; init; }
}

/// <summary>
/// Pre-warmed, persistent floating toolbar window using native Windows 11 DWM hardware rounded corners &amp; shadow.
/// Zero AllowsTransparency or DropShadowEffect overhead -> instant popup and minimal RAM!
/// </summary>
internal sealed class PopupToolbarWindow : Window
{
    private readonly ConfigService _cfg;
    private readonly Border _card;
    private readonly StackPanel _row;
    private readonly Border _topHighlight;
    private readonly DispatcherTimer _autoHideTimer;
    private readonly Dictionary<string, Action> _hintMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _visibleTips = new();
    private readonly List<(Border ButtonBorder, PopupActionItem Item, string Hint)> _buttonEntries = new();
    private readonly Action<string, string> _onReorder;
    private readonly HoverTipWindow _hoverTip;

    private PopupActionItem _dragItem;
    private Point _dragStartScreen;
    private bool _isDragging;
    private bool _isOpen;

    public IntPtr Hwnd { get; private set; }
    public bool IsPopupOpen => _isOpen;
    public IReadOnlyList<string> VisibleTips => _visibleTips;

    public bool HasHintKey(string key) => _isOpen && _hintMap.ContainsKey(key);

    public void InvokeHintKey(string key)
    {
        if (!_isOpen || !_hintMap.TryGetValue(key, out var fn)) return;
        HideToolbar();
        fn?.Invoke();
    }

    public PopupToolbarWindow(ConfigService cfg, Action<string, string> onReorder)
    {
        _cfg = cfg;
        _onReorder = onReorder;
        var pal = cfg.Palette;

        W11WindowHelper.ConfigureDwmFlyoutChrome(this);

        _row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        _topHighlight = new Border
        {
            Height = 1,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(pal.IsLight ? Colors.White : Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(0, -5, 0, 0),
            IsHitTestVisible = false
        };

        var innerGrid = new Grid();
        innerGrid.Children.Add(_topHighlight);
        innerGrid.Children.Add(_row);

        _card = new Border
        {
            Padding = new Thickness(4, 5, 4, 5),
            Background = new LinearGradientBrush(pal.BgTop, pal.BgBottom, 90),
            Child = innerGrid
        };

        Content = _card;

        _hoverTip = new HoverTipWindow(cfg);

        _autoHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(500, cfg.PopupTimeoutMs)) };
        _autoHideTimer.Tick += (_, _) =>
        {
            if (IsMouseOver)
            {
                ResetAutoHide();
                return;
            }
            HideToolbar();
        };
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
        _hoverTip.PreWarm();
    }

    public void PopulateActions(IReadOnlyList<PopupActionItem> actions)
    {
        var pal = _cfg.Palette;
        if (Hwnd != IntPtr.Zero)
            W11WindowHelper.ApplyDwmFlyoutAttributes(Hwnd, pal);

        _card.Background = new LinearGradientBrush(pal.BgTop, pal.BgBottom, 90);
        _topHighlight.Background = new SolidColorBrush(pal.IsLight ? Colors.White : Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));

        _row.Children.Clear();
        _visibleTips.Clear();
        _hintMap.Clear();
        _buttonEntries.Clear();

        var textBrush = new SolidColorBrush(pal.Text);
        var accentTextBrush = new SolidColorBrush(pal.AccentText);
        var accentBarBrush = new SolidColorBrush(pal.Accent);
        var sepBrush = new SolidColorBrush(pal.Separator);
        var hoverBg = new LinearGradientBrush(pal.HoverTop, pal.HoverBottom, 90);
        var hoverBorder = new SolidColorBrush(pal.HoverBorder.Value);
        var pillFill = new SolidColorBrush(pal.PillFill);
        var pillBorder = new SolidColorBrush(pal.PillBorder);
        var surfaceBrush = new SolidColorBrush(pal.Surface);
        var iconFont = new FontFamily(_cfg.IconFontFamily);
        var textFont = new FontFamily("Segoe UI Variable Text, Segoe UI");

        int prevGrp = -1;
        for (int i = 0; i < actions.Count; i++)
        {
            var act = actions[i];
            int idx = i + 1;
            string hint = SmartParser.HintForIndex(idx);

            _visibleTips.Add(act.Tip);
            if (act.OnClick != null)
                _hintMap[hint] = act.OnClick;

            if (prevGrp != -1 && act.Group != prevGrp)
            {
                _row.Children.Add(new Border
                {
                    Width = 1,
                    Height = 18,
                    Background = sepBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(3, 0, 3, 0)
                });
            }
            prevGrp = act.Group;

            bool useIcon = !act.ForceText && _cfg.GetActionDisplay(act.Tip) == "icon";
            string labelShown = useIcon
                ? act.Glyph
                : (!string.IsNullOrEmpty(act.ShortLabel) ? act.ShortLabel : act.Tip);
            bool isIconFont = useIcon && act.IsIconGlyph;

            var btnGrid = new Grid
            {
                MinWidth = useIcon ? 36 : 40,
                MaxWidth = 220,
                Height = 32
            };

            if (act.SwatchColor.HasValue)
            {
                btnGrid.Children.Add(new Border
                {
                    Width = 18,
                    Height = 18,
                    CornerRadius = new CornerRadius(5),
                    Background = new SolidColorBrush(act.SwatchColor.Value),
                    BorderBrush = new SolidColorBrush(pal.IsLight ? Color.FromRgb(0x47, 0x55, 0x69) : Colors.White),
                    BorderThickness = new Thickness(1),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
            else if (useIcon && act.Image != null)
            {
                var tile = new Border
                {
                    Width = 26,
                    Height = 26,
                    CornerRadius = new CornerRadius(6),
                    Background = new SolidColorBrush(pal.FaviconTile),
                    BorderBrush = new SolidColorBrush(pal.IsLight ? Color.FromRgb(0xCB, 0xD5, 0xE1) : Color.FromRgb(0x32, 0x38, 0x4C)),
                    BorderThickness = new Thickness(1),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var img = new Image
                {
                    Width = 20,
                    Height = 20,
                    Source = act.Image,
                    Stretch = Stretch.Uniform
                };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                tile.Child = img;
                btnGrid.Children.Add(tile);
            }
            else
            {
                var tb = new TextBlock
                {
                    Text = labelShown,
                    FontFamily = isIconFont ? iconFont : textFont,
                    FontWeight = isIconFont ? FontWeights.Normal : (act.Accent ? FontWeights.Bold : FontWeights.SemiBold),
                    FontSize = isIconFont ? 15 : 13,
                    Foreground = act.Accent ? accentTextBrush : textBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = useIcon ? new Thickness(0) : new Thickness(10, 0, 10, 0)
                };
                btnGrid.Children.Add(tb);
            }

            var bottomBar = new Border
            {
                Width = 12,
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
                    FontFamily = textFont,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 8.5,
                    Foreground = new SolidColorBrush(Color.FromArgb(200, pal.SubText.R, pal.SubText.G, pal.SubText.B)),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 2, 1)
                });
            }

            var btnBorder = new Border
            {
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(2, 0, 2, 0),
                Cursor = Cursors.Hand,
                Child = btnGrid
            };

            void ApplyNormalVisual(bool hovered, bool pressed)
            {
                bottomBar.Visibility = hovered ? Visibility.Visible : Visibility.Hidden;
                btnGrid.RenderTransform = pressed ? new TranslateTransform(0, 1) : Transform.Identity;

                if (act.Accent)
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
                else if (!useIcon && !act.SwatchColor.HasValue)
                {
                    btnBorder.Background = pillFill;
                    btnBorder.BorderBrush = pillBorder;
                }
                else
                {
                    btnBorder.Background = Brushes.Transparent;
                    btnBorder.BorderBrush = Brushes.Transparent;
                }
            }

            ApplyNormalVisual(false, false);

            btnBorder.MouseEnter += (_, _) =>
            {
                ResetAutoHide();
                ApplyNormalVisual(true, false);
                if (!_isDragging)
                    ShowHoverTooltipFor(btnBorder, act.Tip, hint);
            };

            btnBorder.MouseLeave += (_, _) =>
            {
                ApplyNormalVisual(false, false);
                if (!_isDragging)
                    _hoverTip.HideTip();
            };

            btnBorder.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                ApplyNormalVisual(true, true);
                _dragItem = act;
                _dragStartScreen = PointToScreen(e.GetPosition(this));
                _isDragging = false;
                btnBorder.CaptureMouse();
            };

            btnBorder.MouseMove += (_, e) =>
            {
                if (_dragItem != act || e.LeftButton != MouseButtonState.Pressed) return;
                Point curScreen = PointToScreen(e.GetPosition(this));
                if (!_isDragging)
                {
                    if (Math.Abs(curScreen.X - _dragStartScreen.X) + Math.Abs(curScreen.Y - _dragStartScreen.Y) < _cfg.MinDragPx)
                        return;
                    _isDragging = true;
                }
                var target = HitTestButtonAtScreen(curScreen);
                string dragLabel = target != null && target.Tip != act.Tip
                    ? $"Move '{act.Tip}' before '{target.Tip}'"
                    : $"Dragging: {act.Tip}";
                ShowHoverTooltipFor(btnBorder, dragLabel, "");
            };

            btnBorder.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (btnBorder.IsMouseCaptured)
                    btnBorder.ReleaseMouseCapture();
                ApplyNormalVisual(btnBorder.IsMouseOver, false);

                if (_dragItem == act)
                {
                    bool wasDrag = _isDragging;
                    _dragItem = null;
                    _isDragging = false;

                    if (wasDrag)
                    {
                        Point curScreen = PointToScreen(e.GetPosition(this));
                        var target = HitTestButtonAtScreen(curScreen);
                        _hoverTip.HideTip();
                        if (target != null && target.Tip != act.Tip)
                            _onReorder?.Invoke(act.Tip, target.Tip);
                        return;
                    }
                }

                _hoverTip.HideTip();
                act.OnClick?.Invoke();
            };

            btnBorder.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                _hoverTip.HideTip();
                if (act.OnRightClick != null)
                    act.OnRightClick.Invoke();
                else
                    HideToolbar();
            };

            _buttonEntries.Add((btnBorder, act, hint));
            _row.Children.Add(btnBorder);
        }
    }

    public void ShowActionsAt(IReadOnlyList<PopupActionItem> actions, int screenX, int screenY, bool instant = false)
    {
        PopulateActions(actions);
        _hoverTip.HideTip();

        if (Hwnd != IntPtr.Zero)
            Native.RemoveExStyle(Hwnd, Native.WS_EX_TRANSPARENT);

        if (!IsVisible)
            Show();
        UpdateLayout();

        double scale = Native.ScaleAt(screenX, screenY);
        var work = Native.WorkAreaAt(screenX, screenY, out _);
        int physW = (int)Math.Round(ActualWidth * scale);
        int physH = (int)Math.Round(ActualHeight * scale);
        int pad = (int)Math.Round(6 * scale);

        int px = Math.Max(work.Left + pad, Math.Min(screenX - physW / 3, work.Right - physW - pad));
        int py = screenY - physH - (int)Math.Round(18 * scale);
        if (py < work.Top + pad)
            py = screenY + (int)Math.Round(24 * scale);
        py = Math.Max(work.Top + pad, Math.Min(py, work.Bottom - physH - pad));

        Native.MoveWindowPhysical(Hwnd, px, py);
        _isOpen = true;

        W11WindowHelper.AnimateFlyoutIn(this, _card, _cfg.FadeInMs, !instant && W11WindowHelper.ShouldAnimate(_cfg));
        ResetAutoHide();
    }

    public void HideToolbar()
    {
        if (!_isOpen && !IsVisible) return;
        _isOpen = false;
        _autoHideTimer.Stop();
        _hoverTip.HideTip();
        _card.BeginAnimation(UIElement.OpacityProperty, null);
        Hide();
    }

    private PopupActionItem HitTestButtonAtScreen(Point screenPt)
    {
        foreach (var (border, item, _) in _buttonEntries)
        {
            try
            {
                Point tl = border.PointToScreen(new Point(0, 0));
                Point br = border.PointToScreen(new Point(border.ActualWidth, border.ActualHeight));
                if (screenPt.X >= tl.X && screenPt.X <= br.X && screenPt.Y >= tl.Y - 20 && screenPt.Y <= br.Y + 20)
                    return item;
            }
            catch { }
        }
        return null;
    }

    private void ShowHoverTooltipFor(Border btnBorder, string text, string hint)
    {
        try
        {
            Point centerBottom = btnBorder.PointToScreen(new Point(btnBorder.ActualWidth / 2.0, btnBorder.ActualHeight));
            Point cardBottom   = _card.PointToScreen(new Point(0, _card.ActualHeight));
            Point cardTop      = _card.PointToScreen(new Point(0, 0));
            _hoverTip.ShowAnchored(text, hint, (int)centerBottom.X, (int)cardBottom.Y + 5, (int)cardTop.Y - 5);
        }
        catch { }
    }

    private void ResetAutoHide()
    {
        _autoHideTimer.Stop();
        _autoHideTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(500, _cfg.PopupTimeoutMs));
        _autoHideTimer.Start();
    }
}

internal sealed class HoverTipWindow : Window
{
    private readonly ConfigService _cfg;
    private readonly Border _card;
    private readonly TextBlock _labelTb;
    private readonly Border _keycapBorder;
    private readonly TextBlock _keycapTb;
    private IntPtr _hwnd;

    public HoverTipWindow(ConfigService cfg)
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

        _labelTb = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            Foreground = new SolidColorBrush(pal.Text),
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(_labelTb);

        _keycapTb = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 10,
            Foreground = new SolidColorBrush(pal.KeycapText),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        _keycapBorder = new Border
        {
            MinWidth = 19,
            Height = 18,
            Padding = new Thickness(4, 0, 4, 0),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(pal.KeycapBg),
            BorderBrush = new SolidColorBrush(pal.IsLight ? Color.FromRgb(0xCB, 0xD5, 0xE1) : Color.FromRgb(0x38, 0x40, 0x56)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = _keycapTb
        };
        row.Children.Add(_keycapBorder);

        _card = new Border
        {
            Padding = new Thickness(9, 4, 8, 4),
            Background = new LinearGradientBrush(pal.BgTop, pal.BgBottom, 90),
            Child = row
        };

        Content = _card;
    }

    public void PreWarm()
    {
        W11WindowHelper.MakeNonActivatingFlyout(this, _cfg.Palette, clickThrough: true, smallCorners: true);
        _hwnd = new WindowInteropHelper(this).Handle;
        Left = -32000;
        Top = -32000;
        Show();
        UpdateLayout();
        Hide();
    }

    public void ShowAnchored(string labelText, string keyHint, int screenAnchorX, int screenBelowY, int screenAboveY)
    {
        var pal = _cfg.Palette;
        if (_hwnd != IntPtr.Zero)
            W11WindowHelper.ApplyDwmFlyoutAttributes(_hwnd, pal, smallCorners: true);

        _card.Background = new LinearGradientBrush(pal.BgTop, pal.BgBottom, 90);
        _labelTb.Foreground = new SolidColorBrush(pal.Text);
        _labelTb.Text = labelText;

        if (!string.IsNullOrEmpty(keyHint))
        {
            _keycapBorder.Visibility = Visibility.Visible;
            _keycapBorder.Background = new SolidColorBrush(pal.KeycapBg);
            _keycapTb.Foreground = new SolidColorBrush(pal.KeycapText);
            _keycapTb.Text = keyHint;
        }
        else
        {
            _keycapBorder.Visibility = Visibility.Collapsed;
        }

        if (!IsVisible)
            Show();
        UpdateLayout();

        double scale = Native.ScaleAt(screenAnchorX, screenBelowY);
        var work = Native.WorkAreaAt(screenAnchorX, screenBelowY, out _);
        int physW = (int)Math.Round(ActualWidth * scale);
        int physH = (int)Math.Round(ActualHeight * scale);
        int pad = (int)Math.Round(6 * scale);

        int tx = Math.Max(work.Left + pad, Math.Min(screenAnchorX - physW / 2, work.Right - physW - pad));
        int ty = (screenBelowY + physH > work.Bottom - pad) ? (screenAboveY - physH) : screenBelowY;

        Native.MoveWindowPhysical(_hwnd, tx, ty);
    }

    public void HideTip()
    {
        if (IsVisible)
            Hide();
    }
}

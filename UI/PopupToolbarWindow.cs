using System;
using System.Collections.Generic;
using System.Linq;
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
    public string BadgeGlyph { get; init; } = "";
    public bool IsIconGlyph { get; init; }
    public BitmapSource Image { get; init; }
    public Color? SwatchColor { get; init; }
    public bool ForceText { get; init; }
    public bool Accent { get; init; }
    public int Group { get; init; }
    /// <summary>
    /// Relevance tier for 2-layer packing (0 = contextual match, 1 = core edit/search, 2 = frequent secondary, 3 = utility).
    /// Lower numbers strongly prefer the lower layer closest to the cursor.
    /// </summary>
    public int RelevancePriority { get; init; } = 2;
    public Action OnClick { get; init; }
    public Action OnRightClick { get; init; }
}

/// <summary>
/// Pre-warmed, persistent floating toolbar window supporting 4 sharp size presets (mini, compact, normal, large)
/// and a symmetrical width-balanced 2-layer smart context deck.
/// </summary>
internal sealed class PopupToolbarWindow : Window
{
    private readonly ConfigService _cfg;
    private readonly Border _card;
    private readonly StackPanel _deckPanel;
    private readonly StackPanel _upperSectionStack;
    private readonly Grid _tuiTopHost;
    private readonly Grid _rowsHost;
    private readonly StackPanel _rowsStack;
    private readonly Grid _upperRow;
    private readonly Border _layerDivider;
    private readonly Grid _lowerRow;
    private readonly Grid _tuiBottomHost;
    private readonly Border _topHighlight;
    private readonly DispatcherTimer _autoHideTimer;
    private readonly Dictionary<string, Action> _hintMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _visibleTips = new();
    private readonly List<(Border ButtonBorder, PopupActionItem Item, string Hint)> _buttonEntries = new();
    private readonly Action<string, string> _onReorder;
    private readonly HoverTipWindow _hoverTip;
    private TextBlock _tuiTickerTb;
    private string _tuiDefaultTicker = "✦ SparkBar";

    private PopupActionItem _dragItem;
    private Point _dragStartScreen;
    private bool _isDragging;
    private bool _isOpen;

    public IntPtr Hwnd { get; private set; }
    public bool IsPopupOpen => _isOpen;
    public Native.RECT LastScreenRect { get; private set; }
    public IReadOnlyList<string> VisibleTips => _visibleTips;

    public bool HasHintKey(string key) => _isOpen && _hintMap.ContainsKey(key);

    public void InvokeHintKey(string key)
    {
        if (!_isOpen || !_hintMap.TryGetValue(key, out var fn)) return;
        HideToolbar();
        fn?.Invoke();
    }

    private FlyoutMetrics GetMetrics() => W11WindowHelper.GetFlyoutMetrics(_cfg);

    public PopupToolbarWindow(ConfigService cfg, Action<string, string> onReorder)
    {
        _cfg = cfg;
        _onReorder = onReorder;
        var pal = cfg.Palette;

        W11WindowHelper.ConfigureDwmFlyoutChrome(this);

        _upperRow = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center
        };

        _layerDivider = new Border
        {
            Height = 1,
            Margin = new Thickness(2, 2, 2, 2),
            SnapsToDevicePixels = true,
            Visibility = Visibility.Collapsed
        };
        RenderOptions.SetEdgeMode(_layerDivider, EdgeMode.Aliased);

        _lowerRow = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center
        };

        _rowsStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center
        };

        _tuiTopHost = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch, Visibility = Visibility.Collapsed };
        _rowsHost = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        _upperSectionStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _upperSectionStack.Children.Add(_tuiTopHost);
        _upperSectionStack.Children.Add(_rowsHost);

        _tuiBottomHost = new Grid { HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
        _tuiBottomHost.SetBinding(WidthProperty, new System.Windows.Data.Binding(nameof(ActualWidth))
        {
            Source = _upperSectionStack,
            Mode = System.Windows.Data.BindingMode.OneWay
        });

        _deckPanel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center
        };
        _deckPanel.Children.Add(_upperSectionStack);
        _deckPanel.Children.Add(_tuiBottomHost);

        _topHighlight = new Border
        {
            Height = 1,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(pal.IsLight ? Colors.White : Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(0, -3, 0, 0),
            SnapsToDevicePixels = true,
            IsHitTestVisible = false
        };
        RenderOptions.SetEdgeMode(_topHighlight, EdgeMode.Aliased);

        var innerGrid = new Grid();
        innerGrid.Children.Add(_topHighlight);
        innerGrid.Children.Add(_deckPanel);

        _card = new Border
        {
            Padding = new Thickness(3, 3, 3, 3),
            Background = new LinearGradientBrush(pal.BgTop, pal.BgBottom, 90),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
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
        W11WindowHelper.MakeNonActivatingFlyout(this, _cfg.Palette, neonTui: _cfg.IsNeonTui);
        Hwnd = new WindowInteropHelper(this).Handle;
        Left = -32000;
        Top = -32000;
        Show();
        UpdateLayout();
        Hide();
        _hoverTip.PreWarm();
    }

    private double EstimateItemWidth(PopupActionItem act, FlyoutMetrics m)
    {
        bool useIcon = !act.ForceText && _cfg.GetActionDisplay(act.Tip) == "icon";
        double marginW = m.BtnMargin.Left + m.BtnMargin.Right;
        if (useIcon || act.SwatchColor.HasValue)
            return m.IconMinWidth + marginW;

        string labelShown = !string.IsNullOrEmpty(act.ShortLabel) ? act.ShortLabel : act.Tip;
        double textW = Math.Ceiling(labelShown.Length * (m.TextFontSize * 0.56)) + (m.TextPadX * 2) + 2;
        return Math.Max(m.PillMinWidth, Math.Min(180, textW)) + marginW;
    }

    /// <summary>
    /// Splits actions into Lower Layer (most relevant, closest to cursor) and Upper Layer (secondary utilities)
    /// while balancing the natural visual width of both rows so they are as symmetrical and compact as possible.
    /// </summary>
    private void PartitionTwoLayersSymmetrically(
        IReadOnlyList<PopupActionItem> actions,
        FlyoutMetrics m,
        out List<PopupActionItem> lowerItems,
        out List<PopupActionItem> upperItems)
    {
        int n = actions.Count;
        double[] widths = new double[n];
        double totalW = 0;
        for (int i = 0; i < n; i++)
        {
            widths[i] = EstimateItemWidth(actions[i], m);
            totalW += widths[i];
        }

        double targetW = totalW / 2.0;

        // Order candidate indices by (RelevancePriority ASC, OriginalIndex ASC)
        int[] ranked = Enumerable.Range(0, n)
            .OrderBy(i => actions[i].RelevancePriority)
            .ThenBy(i => i)
            .ToArray();

        bool[] inLower = new bool[n];
        double curLowerW = 0;
        int lowerCount = 0;

        for (int r = 0; r < n; r++)
        {
            int idx = ranked[r];
            double w = widths[idx];
            if (lowerCount < 2 || Math.Abs((curLowerW + w) - targetW) <= Math.Abs(curLowerW - targetW))
            {
                if (lowerCount < n - 2)
                {
                    inLower[idx] = true;
                    curLowerW += w;
                    lowerCount++;
                }
            }
        }

        // Refine by testing 1-item moves or swaps among items with RelevancePriority >= 1
        // to minimize |lowerW - upperW| without demoting Priority 0 contextual items
        bool improved = true;
        while (improved)
        {
            improved = false;
            double curDiff = Math.Abs(curLowerW - (totalW - curLowerW));

            // Try swapping a lower item (Priority >= 1) with an upper item (Priority <= lower.Priority + 1)
            for (int i = 0; i < n; i++)
            {
                if (!inLower[i] || actions[i].RelevancePriority == 0) continue;
                for (int j = 0; j < n; j++)
                {
                    if (inLower[j]) continue;
                    if (actions[j].RelevancePriority > actions[i].RelevancePriority) continue;

                    double newLowerW = curLowerW - widths[i] + widths[j];
                    double newDiff = Math.Abs(newLowerW - (totalW - newLowerW));
                    if (newDiff + 1.5 < curDiff)
                    {
                        inLower[i] = false;
                        inLower[j] = true;
                        curLowerW = newLowerW;
                        curDiff = newDiff;
                        improved = true;
                        break;
                    }
                }
                if (improved) break;
            }
        }

        lowerItems = new List<PopupActionItem>(lowerCount);
        upperItems = new List<PopupActionItem>(n - lowerCount);
        for (int i = 0; i < n; i++)
        {
            if (inLower[i]) lowerItems.Add(actions[i]);
            else upperItems.Add(actions[i]);
        }
    }

    public void PopulateActions(IReadOnlyList<PopupActionItem> actions)
    {
        var pal = _cfg.Palette;
        var m = GetMetrics();
        bool isTui = _cfg.IsNeonTui;

        Background = new SolidColorBrush(pal.BgBottom);
        if (Hwnd != IntPtr.Zero)
            W11WindowHelper.ApplyDwmFlyoutAttributes(Hwnd, pal, smallCorners: m.SmallWindowCorners, neonTui: isTui);

        W11WindowHelper.ApplyNeonTuiCardChrome(_card, pal, isTui, m.CardPadding);
        _topHighlight.Visibility = isTui ? Visibility.Collapsed : Visibility.Visible;
        _topHighlight.Background = new SolidColorBrush(pal.IsLight ? Colors.White : Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        _layerDivider.Background = new SolidColorBrush(Color.FromArgb(160, pal.Separator.R, pal.Separator.G, pal.Separator.B));

        (_upperRow.Parent as Panel)?.Children.Remove(_upperRow);
        (_lowerRow.Parent as Panel)?.Children.Remove(_lowerRow);
        (_layerDivider.Parent as Panel)?.Children.Remove(_layerDivider);
        _upperRow.Children.Clear();
        _upperRow.ColumnDefinitions.Clear();
        _lowerRow.Children.Clear();
        _lowerRow.ColumnDefinitions.Clear();
        _rowsStack.Children.Clear();
        _rowsHost.Children.Clear();
        _tuiTopHost.Children.Clear();
        _tuiTopHost.ColumnDefinitions.Clear();
        _tuiTopHost.RowDefinitions.Clear();
        _tuiBottomHost.Children.Clear();
        _visibleTips.Clear();
        _hintMap.Clear();
        _buttonEntries.Clear();
        _tuiTickerTb = null;

        PopupActionItem calcItem = null;
        PopupActionItem statsItem = null;
        var deckActions = new List<PopupActionItem>(actions.Count);

        foreach (var a in actions)
        {
            if (isTui && calcItem == null &&
                (a.Tip.StartsWith("Copy conversion:", StringComparison.OrdinalIgnoreCase) ||
                 a.Tip.StartsWith("Copy result:", StringComparison.OrdinalIgnoreCase)))
            {
                calcItem = a;
            }
            else if (isTui && statsItem == null &&
                     a.Tip.StartsWith("Words:", StringComparison.OrdinalIgnoreCase))
            {
                statsItem = a;
            }
            else
            {
                deckActions.Add(a);
                _visibleTips.Add(a.Tip);
            }
        }

        if (isTui && deckActions.Count == 0 && (calcItem != null || statsItem != null))
        {
            if (calcItem != null) { deckActions.Add(calcItem); _visibleTips.Add(calcItem.Tip); calcItem = null; }
            if (statsItem != null) { deckActions.Add(statsItem); _visibleTips.Add(statsItem.Tip); statsItem = null; }
        }

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
        var textFont = isTui ? W11WindowHelper.MonoFont : new FontFamily("Segoe UI Variable Text, Segoe UI");
        var tuiFrameBrush = new SolidColorBrush(W11WindowHelper.GetTuiHighlightColor(pal));
        var tuiAccentBrush = new SolidColorBrush(W11WindowHelper.GetTuiAccentColor(pal));

        int hintCounter = 1;

        void PopulateRow(Grid targetGrid, IReadOnlyList<PopupActionItem> rowActions)
        {
            int prevGrp = -1;
            int colIdx = 0;

            for (int i = 0; i < rowActions.Count; i++)
            {
                var act = rowActions[i];
                string hint = SmartParser.HintForIndex(hintCounter++);
                if (act.OnClick != null)
                    _hintMap[hint] = act.OnClick;

                if (prevGrp != -1 && act.Group != prevGrp)
                {
                    targetGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    UIElement sepEl;
                    if (isTui)
                    {
                        sepEl = new TextBlock
                        {
                            Text = "│",
                            FontFamily = W11WindowHelper.MonoFont,
                            FontSize = m.TextFontSize + 1,
                            Foreground = tuiFrameBrush,
                            Opacity = 0.65,
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(0.5, 0, 0.5, 0),
                            IsHitTestVisible = false
                        };
                    }
                    else
                    {
                        var vSep = new Border
                        {
                            Width = 1,
                            Height = m.SepHeight,
                            Background = sepBrush,
                            VerticalAlignment = VerticalAlignment.Center,
                            SnapsToDevicePixels = true,
                            Margin = new Thickness(1.5, 0, 1.5, 0)
                        };
                        RenderOptions.SetEdgeMode(vSep, EdgeMode.Aliased);
                        sepEl = vSep;
                    }
                    Grid.SetColumn(sepEl, colIdx++);
                    targetGrid.Children.Add(sepEl);
                }
                prevGrp = act.Group;

                bool useIcon = !act.ForceText && _cfg.GetActionDisplay(act.Tip) == "icon";
                string labelShown = useIcon
                    ? act.Glyph
                    : (!string.IsNullOrEmpty(act.ShortLabel) ? act.ShortLabel : act.Tip);
                bool isIconFont = useIcon && act.IsIconGlyph;

                double natW = EstimateItemWidth(act, m);
                // Proportional star sizing with MinWidth = natW ensures both rows stretch flush to the exact same outer width!
                targetGrid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(natW, GridUnitType.Star),
                    MinWidth = natW
                });

                var btnGrid = new Grid
                {
                    MinWidth = useIcon ? m.IconMinWidth : m.PillMinWidth,
                    MaxWidth = 200,
                    Height = m.BtnHeight,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    SnapsToDevicePixels = true,
                    UseLayoutRounding = true
                };

                if (act.SwatchColor.HasValue)
                {
                    btnGrid.Children.Add(new Border
                    {
                        Width = m.SwatchSize,
                        Height = m.SwatchSize,
                        CornerRadius = new CornerRadius(3.5),
                        Background = new SolidColorBrush(act.SwatchColor.Value),
                        BorderBrush = new SolidColorBrush(pal.IsLight ? Color.FromRgb(0x47, 0x55, 0x69) : Colors.White),
                        BorderThickness = new Thickness(1),
                        SnapsToDevicePixels = true,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    });
                }
                else if (useIcon && act.Image != null)
                {
                    var tile = new Border
                    {
                        Width = m.FavTileSize,
                        Height = m.FavTileSize,
                        CornerRadius = new CornerRadius(4.5),
                        Background = new SolidColorBrush(pal.FaviconTile),
                        BorderBrush = new SolidColorBrush(pal.IsLight ? Color.FromRgb(0xCB, 0xD5, 0xE1) : Color.FromRgb(0x32, 0x38, 0x4C)),
                        BorderThickness = new Thickness(1),
                        SnapsToDevicePixels = true,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    var img = new Image
                    {
                        Width = m.FavImgSize,
                        Height = m.FavImgSize,
                        Source = act.Image,
                        Stretch = Stretch.Uniform,
                        SnapsToDevicePixels = true
                    };
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                    tile.Child = img;
                    btnGrid.Children.Add(tile);
                }
                else
                {
                    bool hasIconBadge = useIcon && !string.IsNullOrEmpty(act.BadgeGlyph);
                    var tb = new TextBlock
                    {
                        Text = labelShown,
                        FontFamily = isIconFont ? iconFont : textFont,
                        FontWeight = isIconFont ? FontWeights.Normal : (act.Accent ? FontWeights.Bold : FontWeights.SemiBold),
                        FontSize = isIconFont ? m.IconFontSize : m.TextFontSize,
                        Foreground = act.Accent ? accentTextBrush : textBrush,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        SnapsToDevicePixels = true,
                        Margin = !useIcon
                            ? new Thickness(m.TextPadX, 0, m.TextPadX, 0)
                            : (hasIconBadge ? new Thickness(-2.5, 1.5, 0, 0) : new Thickness(0))
                    };
                    TextOptions.SetTextFormattingMode(tb, TextFormattingMode.Display);
                    btnGrid.Children.Add(tb);

                    if (hasIconBadge)
                    {
                        double badgeFsz = Math.Max(8.0, Math.Round(m.IconFontSize * 0.56, 1));
                        var badgeTb = new TextBlock
                        {
                            Text = act.BadgeGlyph,
                            FontFamily = W11WindowHelper.MonoFont,
                            FontWeight = FontWeights.Bold,
                            FontSize = badgeFsz,
                            LineHeight = badgeFsz,
                            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                            Foreground = isTui ? tuiAccentBrush : accentTextBrush,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            VerticalAlignment = VerticalAlignment.Top,
                            Margin = new Thickness(0, 1.5, 2.5, 0),
                            IsHitTestVisible = false,
                            SnapsToDevicePixels = true
                        };
                        TextOptions.SetTextFormattingMode(badgeTb, TextFormattingMode.Display);
                        btnGrid.Children.Add(badgeTb);
                    }
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
                        FontFamily = textFont,
                        FontWeight = FontWeights.SemiBold,
                        FontSize = 7.5,
                        Foreground = new SolidColorBrush(Color.FromArgb(195, pal.SubText.R, pal.SubText.G, pal.SubText.B)),
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Bottom,
                        Margin = new Thickness(0, 0, 1.5, 0)
                    });
                }

                var btnBorder = new Border
                {
                    CornerRadius = new CornerRadius(m.BtnCornerRadius),
                    BorderThickness = new Thickness(1),
                    Margin = m.BtnMargin,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    SnapsToDevicePixels = true,
                    UseLayoutRounding = true,
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
                    {
                        if (_cfg.IsNeonTui && _tuiTickerTb != null)
                            _tuiTickerTb.Text = _tuiDefaultTicker;
                        else
                            _hoverTip.HideTip();
                    }
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

                Grid.SetColumn(btnBorder, colIdx++);
                _buttonEntries.Add((btnBorder, act, hint));
                targetGrid.Children.Add(btnBorder);
            }
        }

        if (_cfg.TwoLayerPopup && deckActions.Count >= 4)
        {
            PartitionTwoLayersSymmetrically(deckActions, m, out var lowerItems, out var upperItems);

            // Number lower (relevant) row 1..K first so primary actions have 1, 2, 3... shortcuts
            PopulateRow(_lowerRow, lowerItems);
            PopulateRow(_upperRow, upperItems);
            _upperRow.Visibility = Visibility.Visible;

            if (isTui)
            {
                _upperRow.Margin = new Thickness(3, 2, 3, 2);
                _lowerRow.Margin = new Thickness(3, 2, 3, 2);
                _rowsStack.Children.Add(_upperRow);
                _rowsStack.Children.Add(W11WindowHelper.CreateTuiHorizontalDivider(pal, m));
                _rowsStack.Children.Add(_lowerRow);
            }
            else
            {
                _upperRow.Margin = new Thickness(0);
                _lowerRow.Margin = new Thickness(0);
                _layerDivider.Visibility = Visibility.Visible;
                _rowsStack.Children.Add(_upperRow);
                _rowsStack.Children.Add(_layerDivider);
                _rowsStack.Children.Add(_lowerRow);
            }
        }
        else
        {
            _upperRow.Visibility = Visibility.Collapsed;
            _layerDivider.Visibility = Visibility.Collapsed;
            PopulateRow(_lowerRow, deckActions);

            _lowerRow.Margin = isTui ? new Thickness(3, 2, 3, 2) : new Thickness(0);
            _rowsStack.Children.Add(_lowerRow);
        }

        _rowsHost.Children.Add(_rowsStack);

        if (isTui)
        {
            // Flush Neon TUI header band with Calculate/Convert & Word Count embedded right inside
            double topFsz = Math.Max(9.0, m.MenuHeaderFontSize + 0.5);
            double topRowH = Math.Ceiling(topFsz * 1.45);
            _tuiTopHost.Visibility = Visibility.Visible;
            _tuiBottomHost.Visibility = Visibility.Visible;

            _tuiTopHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _tuiTopHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var topBarGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
            topBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // badges
            topBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // dashes

            TextBlock MakeBorderGlyph(string s, Brush fg, bool bold = false)
            {
                var t = new TextBlock
                {
                    Text = s,
                    FontFamily = W11WindowHelper.MonoFont,
                    FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                    FontSize = topFsz,
                    LineHeight = topRowH,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    Foreground = fg,
                    VerticalAlignment = VerticalAlignment.Center
                };
                TextOptions.SetTextFormattingMode(t, TextFormattingMode.Display);
                return t;
            }

            var badgeRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            Border BuildTopBorderBadge(string txt, string tipText, bool primaryShade, Action onClick, Action onRightClick = null)
            {
                var label = MakeBorderGlyph(primaryShade ? $"░▒▓ {txt} ▓▒░" : $"░▒ {txt} ▒░", tuiAccentBrush, bold: true);
                var pill = new Border
                {
                    CornerRadius = new CornerRadius(2.5),
                    Padding = new Thickness(2, 0, 2, 0),
                    Cursor = Cursors.Hand,
                    Background = Brushes.Transparent,
                    Child = label
                };
                pill.MouseEnter += (_, _) =>
                {
                    ResetAutoHide();
                    pill.Background = hoverBg;
                    if (_tuiTickerTb != null) _tuiTickerTb.Text = $"▸ {tipText}";
                };
                pill.MouseLeave += (_, _) =>
                {
                    pill.Background = Brushes.Transparent;
                    if (_tuiTickerTb != null) _tuiTickerTb.Text = _tuiDefaultTicker;
                };
                pill.MouseLeftButtonDown += (_, e) => { e.Handled = true; };
                pill.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick?.Invoke(); };
                pill.MouseRightButtonUp += (_, e) => { e.Handled = true; (onRightClick ?? onClick)?.Invoke(); };
                return pill;
            }

            if (calcItem != null)
            {
                string calcHint = SmartParser.HintForIndex(hintCounter++);
                if (calcItem.OnClick != null) _hintMap[calcHint] = calcItem.OnClick;
                string calcLabel = !string.IsNullOrEmpty(calcItem.ShortLabel) ? calcItem.ShortLabel : calcItem.Glyph;
                badgeRow.Children.Add(BuildTopBorderBadge(calcLabel, $"{calcItem.Tip} [{calcHint}]", primaryShade: true, calcItem.OnClick));
                if (statsItem != null)
                    badgeRow.Children.Add(MakeBorderGlyph("─", tuiFrameBrush));
            }

            if (statsItem != null)
            {
                string statsLabel = (!string.IsNullOrEmpty(statsItem.ShortLabel) ? statsItem.ShortLabel : statsItem.Glyph)
                    .Replace(" ", "·");
                badgeRow.Children.Add(BuildTopBorderBadge(
                    statsLabel,
                    $"{statsItem.Tip} (Click: Inspect · Right-click: Copy)",
                    primaryShade: calcItem == null,
                    statsItem.OnClick,
                    statsItem.OnRightClick));
            }

            if (calcItem == null && statsItem == null)
            {
                badgeRow.Children.Add(MakeBorderGlyph("░▒▓ SPARKBAR ▓▒░", tuiAccentBrush, bold: true));
            }

            badgeRow.Children.Add(MakeBorderGlyph(" ", tuiFrameBrush));
            Grid.SetColumn(badgeRow, 0);
            topBarGrid.Children.Add(badgeRow);

            var topFill = W11WindowHelper.CreateTuiDashFill('─', tuiFrameBrush, topFsz, topRowH);
            Grid.SetColumn(topFill, 1);
            topBarGrid.Children.Add(topFill);

            var tuiHi = W11WindowHelper.GetTuiHighlightColor(pal);
            var topBand = new Border
            {
                CornerRadius = new CornerRadius(2.5, 2.5, 0, 0),
                Background = new SolidColorBrush(pal.IsLight
                    ? Color.FromArgb(14, 0x03, 0x69, 0xA1)
                    : Color.FromArgb(20, tuiHi.R, tuiHi.G, tuiHi.B)),
                Padding = new Thickness(5, 1.5, 5, 1.5),
                Child = topBarGrid
            };
            Grid.SetRow(topBand, 0);
            _tuiTopHost.Children.Add(topBand);

            var topDiv = W11WindowHelper.CreateTuiHorizontalDivider(pal, m);
            Grid.SetRow(topDiv, 1);
            _tuiTopHost.Children.Add(topDiv);

            _tuiDefaultTicker = "✦ Hover icon";
            var botBar = W11WindowHelper.CreateTuiFooterBar(_tuiDefaultTicker, pal, m, out _tuiTickerTb);
            _tuiBottomHost.Children.Add(botBar);
        }
        else
        {
            _tuiTopHost.Visibility = Visibility.Collapsed;
            _tuiBottomHost.Visibility = Visibility.Collapsed;
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

        int px = Math.Max(work.Left + pad, Math.Min(screenX - physW / 2, work.Right - physW - pad));
        int py = screenY - physH - (int)Math.Round(12 * scale);
        if (py < work.Top + pad)
            py = screenY + (int)Math.Round(20 * scale);
        py = Math.Max(work.Top + pad, Math.Min(py, work.Bottom - physH - pad));

        Native.MoveWindowPhysical(Hwnd, px, py);
        LastScreenRect = new Native.RECT { Left = px, Top = py, Right = px + physW, Bottom = py + physH };
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
                if (screenPt.X >= tl.X - 2 && screenPt.X <= br.X + 2 && screenPt.Y >= tl.Y - 4 && screenPt.Y <= br.Y + 4)
                    return item;
            }
            catch { }
        }
        return null;
    }

    private void ShowHoverTooltipFor(Border btnBorder, string text, string hint)
    {
        if (_cfg.IsNeonTui && _tuiTickerTb != null)
        {
            _tuiTickerTb.Text = string.IsNullOrEmpty(hint) ? $"⇄ {text}" : $"▸ {text} [{hint}]";
            return;
        }
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
            FontSize = 11.5,
            Foreground = new SolidColorBrush(pal.Text),
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(_labelTb);

        _keycapTb = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 9.5,
            Foreground = new SolidColorBrush(pal.KeycapText),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        _keycapBorder = new Border
        {
            MinWidth = 18,
            Height = 16,
            Padding = new Thickness(3.5, 0, 3.5, 0),
            CornerRadius = new CornerRadius(3.5),
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
            Padding = new Thickness(8, 3, 7, 3),
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
        var m = W11WindowHelper.GetFlyoutMetrics(_cfg);
        Background = new SolidColorBrush(pal.BgBottom);
        if (_hwnd != IntPtr.Zero)
            W11WindowHelper.ApplyDwmFlyoutAttributes(_hwnd, pal, smallCorners: true);

        _card.Background = new LinearGradientBrush(pal.BgTop, pal.BgBottom, 90);
        _labelTb.FontSize = m.TextFontSize;
        _labelTb.Foreground = new SolidColorBrush(pal.Text);
        _labelTb.Text = labelText;

        if (!string.IsNullOrEmpty(keyHint))
        {
            _keycapBorder.Visibility = Visibility.Visible;
            _keycapBorder.MinWidth = m.KeycapWidth;
            _keycapBorder.Height = m.KeycapHeight;
            _keycapBorder.Background = new SolidColorBrush(pal.KeycapBg);
            _keycapTb.FontSize = m.KeycapFontSize;
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
        int ty = (screenAboveY - physH >= work.Top + pad) ? (screenAboveY - physH) : screenBelowY;

        Native.MoveWindowPhysical(_hwnd, tx, ty);
    }

    public void HideTip()
    {
        if (IsVisible)
            Hide();
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace SparkBar;

internal enum ResultCardMode
{
    Define,
    Translate,
    Inspect
}

/// <summary>
/// Pre-warmed, persistent Windows 11 in-app popup card for Define, Translate, and Text Inspect.
/// Reuses its HWND &amp; DirectX surface for instant &lt; 2ms appearance without opening browser tabs.
/// </summary>
internal sealed class ResultCardWindow : Window
{
    private readonly ConfigService _cfg;
    private readonly Action<string> _onCopyText;
    private readonly Action<IntPtr, string> _onReplaceText;
    private readonly Action<string> _onOpenWebUrl;
    private readonly Action<string> _onToast;

    private readonly Border _card;
    private readonly Border _topHighlight;
    private readonly Grid _headerRow;
    private readonly StackPanel _modeTabRow;
    private readonly Grid _subBar;
    private readonly Border _langPickerPanel;
    private readonly ScrollViewer _scroll;
    private readonly StackPanel _bodyStack;
    private readonly Border _footerSep;
    private readonly WrapPanel _footerRow;

    private readonly Dictionary<string, Action> _hintMap = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource _cts;
    private bool _isOpen;
    private ResultCardMode _mode;
    private string _rawText = "";
    private bool _editable;
    private IntPtr _targetWin;
    private int _anchorX;
    private int _anchorY;
    private string _primaryCopyText = "";
    private bool _langPickerExpanded;

    // Simple session cache for instant repeat opens
    private readonly Dictionary<string, DefinitionLookupResult> _defCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TranslationLookupResult> _transCache = new(StringComparer.Ordinal);

    public IntPtr Hwnd { get; private set; }
    public bool IsCardOpen => _isOpen;

    public bool HasHintKey(string key) => _isOpen && _hintMap.ContainsKey(key);

    public void InvokeHintKey(string key)
    {
        if (!_isOpen || !_hintMap.TryGetValue(key, out var fn)) return;
        fn?.Invoke();
    }

    public ResultCardWindow(
        ConfigService cfg,
        Action<string> onCopyText,
        Action<IntPtr, string> onReplaceText,
        Action<string> onOpenWebUrl,
        Action<string> onToast)
    {
        _cfg = cfg;
        _onCopyText = onCopyText;
        _onReplaceText = onReplaceText;
        _onOpenWebUrl = onOpenWebUrl;
        _onToast = onToast;

        var pal = cfg.Palette;
        W11WindowHelper.ConfigureDwmFlyoutChrome(this);

        _modeTabRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        _headerRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        _headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _headerRow.Children.Add(_modeTabRow);

        _subBar = new Grid
        {
            Margin = new Thickness(0, 0, 0, 6),
            Visibility = Visibility.Collapsed
        };

        _langPickerPanel = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6),
            Margin = new Thickness(0, 0, 0, 6),
            BorderThickness = new Thickness(1),
            Visibility = Visibility.Collapsed
        };

        _bodyStack = new StackPanel
        {
            Orientation = Orientation.Vertical
        };

        _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _bodyStack,
            Padding = new Thickness(0, 0, 2, 0)
        };

        _footerSep = new Border
        {
            Height = 1,
            Margin = new Thickness(0, 8, 0, 7),
            SnapsToDevicePixels = true
        };
        RenderOptions.SetEdgeMode(_footerSep, EdgeMode.Aliased);

        _footerRow = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        var rootStack = new StackPanel { Orientation = Orientation.Vertical };
        rootStack.Children.Add(_headerRow);
        rootStack.Children.Add(_subBar);
        rootStack.Children.Add(_langPickerPanel);
        rootStack.Children.Add(_scroll);
        rootStack.Children.Add(_footerSep);
        rootStack.Children.Add(_footerRow);

        _topHighlight = W11WindowHelper.CreateTopHighlight(pal, topNegativeMargin: -8);

        var innerGrid = new Grid();
        innerGrid.Children.Add(_topHighlight);
        innerGrid.Children.Add(rootStack);

        _card = new Border
        {
            Padding = new Thickness(10, 8, 10, 8),
            Background = new LinearGradientBrush(pal.BgTop, pal.BgBottom, 90),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            Child = innerGrid
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

    public void ShowCard(
        ResultCardMode mode,
        string rawText,
        bool editable,
        IntPtr targetWin,
        int screenX,
        int screenY,
        bool expandLangPicker = false)
    {
        CancelPendingLookup();

        _mode = mode;
        _rawText = rawText ?? "";
        _editable = editable;
        _targetWin = targetWin;
        _anchorX = screenX;
        _anchorY = screenY;
        _langPickerExpanded = expandLangPicker && mode == ResultCardMode.Translate;

        LoadModeContent(isInitialShow: true);
    }

    private void LoadModeContent(bool isInitialShow)
    {
        CancelPendingLookup();

        var pal = _cfg.Palette;
        var m = W11WindowHelper.GetFlyoutMetrics(_cfg);

        Background = new SolidColorBrush(pal.BgBottom);
        if (Hwnd != IntPtr.Zero)
        {
            Native.RemoveExStyle(Hwnd, Native.WS_EX_TRANSPARENT);
            W11WindowHelper.ApplyDwmFlyoutAttributes(Hwnd, pal, smallCorners: m.SmallWindowCorners);
        }

        _card.Width = m.CardWidth;
        _card.Padding = new Thickness(m.CardPadding.Left * 3.0, m.CardPadding.Top * 2.5, m.CardPadding.Right * 3.0, m.CardPadding.Bottom * 2.5);
        _card.Background = new LinearGradientBrush(pal.BgTop, pal.BgBottom, 90);
        _topHighlight.Background = new SolidColorBrush(pal.IsLight ? Colors.White : Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        _topHighlight.Margin = new Thickness(0, -_card.Padding.Top, 0, 0);
        _scroll.MaxHeight = m.CardBodyMaxHeight;
        _footerSep.Background = new SolidColorBrush(pal.Separator);

        BuildHeader(pal, m);
        BuildSubBarAndLangPicker(pal, m);

        if (_mode == ResultCardMode.Inspect)
        {
            RenderInspectBody(pal, m);
            PresentCardWindow(isInitialShow);
            return;
        }

        if (_mode == ResultCardMode.Define)
        {
            string key = _rawText.Trim();
            if (_defCache.TryGetValue(key, out var cached))
            {
                RenderDefinitionResult(cached, pal, m);
                PresentCardWindow(isInitialShow);
                return;
            }

            RenderLoadingState($"Looking up \"{ShortSnippet(key, 28)}\"...", pal, m);
            BuildFooterActions(canCopy: false, canReplace: false, webUrl: ConfigService.DefineUrl + Uri.EscapeDataString(key), pal, m);
            PresentCardWindow(isInitialShow);

            var cts = _cts = new CancellationTokenSource();
            _ = FetchDefinitionAsync(key, cts.Token);
        }
        else if (_mode == ResultCardMode.Translate)
        {
            string trimmed = _rawText.Trim();
            string lang = _cfg.TranslateTargetLang ?? "en";
            string cacheKey = $"{lang}::{trimmed}";
            if (_transCache.TryGetValue(cacheKey, out var cached))
            {
                RenderTranslationResult(cached, pal, m);
                PresentCardWindow(isInitialShow);
                return;
            }

            RenderLoadingState($"Translating to {lang.ToUpperInvariant()}...", pal, m);
            BuildFooterActions(canCopy: false, canReplace: false, webUrl: BuildTranslateWebUrl(trimmed, lang), pal, m);
            PresentCardWindow(isInitialShow);

            var cts = _cts = new CancellationTokenSource();
            _ = FetchTranslationAsync(trimmed, lang, cacheKey, cts.Token);
        }
    }

    private async Task FetchDefinitionAsync(string text, CancellationToken ct)
    {
        var result = await LookupService.LookupDefinitionAsync(text, ct);
        if (ct.IsCancellationRequested || !_isOpen || _mode != ResultCardMode.Define) return;

        if (result.Success)
            _defCache[text] = result;

        await Dispatcher.InvokeAsync(() =>
        {
            if (ct.IsCancellationRequested || !_isOpen || _mode != ResultCardMode.Define) return;
            var pal = _cfg.Palette;
            var m = W11WindowHelper.GetFlyoutMetrics(_cfg);
            RenderDefinitionResult(result, pal, m);
            ClampBottomAfterResize();
        });
    }

    private async Task FetchTranslationAsync(string text, string targetLang, string cacheKey, CancellationToken ct)
    {
        var result = await LookupService.TranslateAsync(text, targetLang, ct);
        if (ct.IsCancellationRequested || !_isOpen || _mode != ResultCardMode.Translate) return;

        if (result.Success)
            _transCache[cacheKey] = result;

        await Dispatcher.InvokeAsync(() =>
        {
            if (ct.IsCancellationRequested || !_isOpen || _mode != ResultCardMode.Translate) return;
            var pal = _cfg.Palette;
            var m = W11WindowHelper.GetFlyoutMetrics(_cfg);
            RenderTranslationResult(result, pal, m);
            ClampBottomAfterResize();
        });
    }

    private void BuildHeader(ThemePalette pal, FlyoutMetrics m)
    {
        _modeTabRow.Children.Clear();
        while (_headerRow.Children.Count > 1)
            _headerRow.Children.RemoveAt(_headerRow.Children.Count - 1);

        var tabs = new (ResultCardMode Mode, string Icon, string Label)[]
        {
            (ResultCardMode.Define, "\uE82D", "Define"),
            (ResultCardMode.Translate, "\uE8C1", "Translate"),
            (ResultCardMode.Inspect, "\uE946", "Inspect")
        };

        var hoverBg = new LinearGradientBrush(pal.HoverTop, pal.HoverBottom, 90);
        var hoverBorder = new SolidColorBrush(pal.HoverBorder.Value);

        foreach (var tab in tabs)
        {
            bool active = _mode == tab.Mode;
            var sp = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            sp.Children.Add(new TextBlock
            {
                Text = tab.Icon,
                FontFamily = new FontFamily(_cfg.IconFontFamily),
                FontSize = Math.Max(9.5, m.MenuHeaderFontSize + 1),
                Foreground = new SolidColorBrush(active ? pal.AccentText : pal.SubText),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            });
            sp.Children.Add(new TextBlock
            {
                Text = tab.Label,
                FontFamily = W11WindowHelper.TextFont,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                FontSize = m.MenuHeaderFontSize + 0.5,
                Foreground = new SolidColorBrush(active ? pal.AccentText : pal.SubText),
                VerticalAlignment = VerticalAlignment.Center
            });

            var pill = new Border
            {
                CornerRadius = new CornerRadius(m.BtnCornerRadius),
                Padding = new Thickness(7, 3, 7, 3),
                Margin = new Thickness(0, 0, 4, 0),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Background = active ? new SolidColorBrush(pal.AccentBgTop) : Brushes.Transparent,
                BorderBrush = active ? new SolidColorBrush(pal.AccentRim) : Brushes.Transparent,
                Child = sp
            };

            var capturedMode = tab.Mode;
            if (!active)
            {
                pill.MouseEnter += (_, _) => { pill.Background = hoverBg; pill.BorderBrush = hoverBorder; };
                pill.MouseLeave += (_, _) => { pill.Background = Brushes.Transparent; pill.BorderBrush = Brushes.Transparent; };
                pill.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    _langPickerExpanded = false;
                    _mode = capturedMode;
                    LoadModeContent(isInitialShow: false);
                };
            }

            _modeTabRow.Children.Add(pill);
        }

        // Compact close button on top-right
        var closeBtn = new Border
        {
            Width = Math.Max(20, m.MenuRowHeight - 1),
            Height = Math.Max(20, m.MenuRowHeight - 1),
            CornerRadius = new CornerRadius(m.BtnCornerRadius),
            BorderThickness = new Thickness(1),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "\uE711",
                FontFamily = new FontFamily(_cfg.IconFontFamily),
                FontSize = 9.5,
                Foreground = new SolidColorBrush(pal.SubText),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        closeBtn.MouseEnter += (_, _) => { closeBtn.Background = hoverBg; closeBtn.BorderBrush = hoverBorder; };
        closeBtn.MouseLeave += (_, _) => { closeBtn.Background = Brushes.Transparent; closeBtn.BorderBrush = Brushes.Transparent; };
        closeBtn.MouseLeftButtonUp += (_, e) => { e.Handled = true; HideCard(); };
        Grid.SetColumn(closeBtn, 1);
        _headerRow.Children.Add(closeBtn);
    }

    private void BuildSubBarAndLangPicker(ThemePalette pal, FlyoutMetrics m)
    {
        _subBar.Children.Clear();
        _subBar.ColumnDefinitions.Clear();
        _langPickerPanel.Visibility = Visibility.Collapsed;
        _langPickerPanel.Child = null;

        if (_mode != ResultCardMode.Translate)
        {
            _subBar.Visibility = Visibility.Collapsed;
            return;
        }

        _subBar.Visibility = Visibility.Visible;
        _subBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _subBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        string curCode = _cfg.TranslateTargetLang ?? "en";
        var foundLang = ConfigService.SupportedTranslateLanguages.FirstOrDefault(x => string.Equals(x.Code, curCode, StringComparison.OrdinalIgnoreCase));
        string curLabel = foundLang.Code != null ? foundLang.Label : curCode.ToUpperInvariant();

        _subBar.Children.Add(new TextBlock
        {
            Text = "Target language:",
            FontFamily = W11WindowHelper.TextFont,
            FontSize = m.CardSubFontSize,
            Foreground = new SolidColorBrush(pal.SubText),
            VerticalAlignment = VerticalAlignment.Center
        });

        var langBtn = new Border
        {
            CornerRadius = new CornerRadius(m.BtnCornerRadius),
            Padding = new Thickness(7, 2.5, 7, 2.5),
            Background = new SolidColorBrush(pal.PillFill),
            BorderBrush = new SolidColorBrush(_langPickerExpanded ? pal.AccentRim : pal.PillBorder),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = curLabel + (_langPickerExpanded ? " ▴" : " ▾"),
                FontFamily = W11WindowHelper.TextFont,
                FontWeight = FontWeights.SemiBold,
                FontSize = m.CardSubFontSize,
                Foreground = new SolidColorBrush(pal.AccentText)
            }
        };
        langBtn.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _langPickerExpanded = !_langPickerExpanded;
            LoadModeContent(isInitialShow: false);
        };
        Grid.SetColumn(langBtn, 1);
        _subBar.Children.Add(langBtn);

        if (_langPickerExpanded)
        {
            _langPickerPanel.Background = new SolidColorBrush(pal.Surface);
            _langPickerPanel.BorderBrush = new SolidColorBrush(pal.Separator);

            var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var (code, label) in ConfigService.SupportedTranslateLanguages)
            {
                bool selected = string.Equals(code, curCode, StringComparison.OrdinalIgnoreCase);
                string shortChip = code.ToUpperInvariant();
                var chip = new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2.5, 6, 2.5),
                    Margin = new Thickness(2),
                    BorderThickness = new Thickness(1),
                    Background = new SolidColorBrush(selected ? pal.AccentBgTop : pal.PillFill),
                    BorderBrush = new SolidColorBrush(selected ? pal.AccentRim : pal.PillBorder),
                    Cursor = Cursors.Hand,
                    ToolTip = label,
                    Child = new TextBlock
                    {
                        Text = shortChip,
                        FontFamily = W11WindowHelper.TextFont,
                        FontWeight = selected ? FontWeights.Bold : FontWeights.SemiBold,
                        FontSize = m.CardSubFontSize - 0.5,
                        Foreground = new SolidColorBrush(selected ? pal.AccentText : pal.Text)
                    }
                };
                string capCode = code;
                chip.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    _cfg.TranslateTargetLang = capCode;
                    _cfg.WriteIni("General", "TranslateTargetLang", capCode);
                    _langPickerExpanded = false;
                    LoadModeContent(isInitialShow: false);
                };
                wrap.Children.Add(chip);
            }
            _langPickerPanel.Child = wrap;
            _langPickerPanel.Visibility = Visibility.Visible;
        }
    }

    private void RenderLoadingState(string msg, ThemePalette pal, FlyoutMetrics m)
    {
        _primaryCopyText = "";
        _bodyStack.Children.Clear();

        var box = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 12, 10, 12),
            Background = new SolidColorBrush(pal.Surface),
            BorderBrush = new SolidColorBrush(pal.Separator),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = "\uE895",
                        FontFamily = new FontFamily(_cfg.IconFontFamily),
                        FontSize = m.CardBodyFontSize,
                        Foreground = new SolidColorBrush(pal.Accent),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 8, 0)
                    },
                    new TextBlock
                    {
                        Text = msg,
                        FontFamily = W11WindowHelper.TextFont,
                        FontSize = m.CardBodyFontSize,
                        Foreground = new SolidColorBrush(pal.SubText),
                        TextWrapping = TextWrapping.Wrap,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            }
        };
        _bodyStack.Children.Add(box);
    }

    private void RenderDefinitionResult(DefinitionLookupResult res, ThemePalette pal, FlyoutMetrics m)
    {
        _bodyStack.Children.Clear();
        string rawTrim = _rawText.Trim();
        string webUrl = ConfigService.DefineUrl + Uri.EscapeDataString(rawTrim);

        if (!res.Success || res.Senses.Count == 0)
        {
            _primaryCopyText = "";
            _bodyStack.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(9, 8, 9, 8),
                Background = new SolidColorBrush(pal.Surface),
                BorderBrush = new SolidColorBrush(pal.Separator),
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = res.ErrorMessage,
                    FontFamily = W11WindowHelper.TextFont,
                    FontSize = m.CardBodyFontSize,
                    Foreground = new SolidColorBrush(pal.SubText),
                    TextWrapping = TextWrapping.Wrap
                }
            });
            BuildFooterActions(canCopy: false, canReplace: false, webUrl, pal, m);
            return;
        }

        // Word title + phonetic + source badge
        var titleWrap = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        titleWrap.Children.Add(new TextBlock
        {
            Text = res.Word,
            FontFamily = W11WindowHelper.DisplayFont,
            FontWeight = FontWeights.SemiBold,
            FontSize = m.CardTitleFontSize,
            Foreground = new SolidColorBrush(pal.Text),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0)
        });

        if (!string.IsNullOrEmpty(res.Phonetic))
        {
            titleWrap.Children.Add(new TextBlock
            {
                Text = res.Phonetic,
                FontFamily = W11WindowHelper.TextFont,
                FontSize = m.CardSubFontSize + 0.5,
                Foreground = new SolidColorBrush(pal.AccentText),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });
        }

        if (!string.IsNullOrEmpty(res.SourceName))
        {
            titleWrap.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 1, 5, 1),
                Background = new SolidColorBrush(pal.PillFill),
                BorderBrush = new SolidColorBrush(pal.PillBorder),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = res.SourceName,
                    FontFamily = W11WindowHelper.TextFont,
                    FontSize = m.CardSubFontSize - 1,
                    Foreground = new SolidColorBrush(pal.SubText)
                }
            });
        }
        _bodyStack.Children.Add(titleWrap);

        var copySb = new StringBuilder();
        copySb.Append(res.Word);
        if (!string.IsNullOrEmpty(res.Phonetic)) copySb.Append($" ({res.Phonetic})");
        copySb.AppendLine(":");

        for (int i = 0; i < res.Senses.Count; i++)
        {
            var s = res.Senses[i];
            if (!string.IsNullOrEmpty(s.PartOfSpeech))
                copySb.AppendLine($"• [{s.PartOfSpeech}] {s.Definition}");
            else
                copySb.AppendLine($"• {s.Definition}");

            var senseCard = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 0, 5) };
            var defLine = new StackPanel { Orientation = Orientation.Horizontal };

            if (!string.IsNullOrEmpty(s.PartOfSpeech))
            {
                senseCard.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(5, 1, 5, 1),
                    Margin = new Thickness(0, 0, 0, 2),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Background = new SolidColorBrush(pal.AccentBgTop),
                    BorderBrush = new SolidColorBrush(pal.AccentRim),
                    BorderThickness = new Thickness(1),
                    Child = new TextBlock
                    {
                        Text = s.PartOfSpeech,
                        FontFamily = W11WindowHelper.TextFont,
                        FontWeight = FontWeights.SemiBold,
                        FontSize = m.CardSubFontSize - 0.5,
                        Foreground = new SolidColorBrush(pal.AccentText)
                    }
                });
            }

            senseCard.Children.Add(new TextBlock
            {
                Text = $"{i + 1}. {s.Definition}",
                FontFamily = W11WindowHelper.TextFont,
                FontSize = m.CardBodyFontSize,
                Foreground = new SolidColorBrush(pal.Text),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = m.CardBodyFontSize * 1.32
            });

            if (!string.IsNullOrEmpty(s.Example))
            {
                senseCard.Children.Add(new TextBlock
                {
                    Text = $"\"{s.Example}\"",
                    FontFamily = W11WindowHelper.TextFont,
                    FontStyle = FontStyles.Italic,
                    FontSize = m.CardSubFontSize,
                    Foreground = new SolidColorBrush(pal.SubText),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(10, 1.5, 0, 0)
                });
            }

            _bodyStack.Children.Add(senseCard);
        }

        if (res.Synonyms.Count > 0)
        {
            var synWrap = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
            synWrap.Children.Add(new TextBlock
            {
                Text = "Similar: ",
                FontFamily = W11WindowHelper.TextFont,
                FontWeight = FontWeights.SemiBold,
                FontSize = m.CardSubFontSize,
                Foreground = new SolidColorBrush(pal.SubText),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 2, 4, 2)
            });

            foreach (string syn in res.Synonyms)
            {
                string capturedSyn = syn;
                var chip = new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 1.5, 6, 1.5),
                    Margin = new Thickness(0, 2, 4, 2),
                    Background = new SolidColorBrush(pal.Surface),
                    BorderBrush = new SolidColorBrush(pal.Separator),
                    BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand,
                    ToolTip = "Click to look up \"" + capturedSyn + "\"",
                    Child = new TextBlock
                    {
                        Text = capturedSyn,
                        FontFamily = W11WindowHelper.TextFont,
                        FontSize = m.CardSubFontSize,
                        Foreground = new SolidColorBrush(pal.Text)
                    }
                };
                chip.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    _rawText = capturedSyn;
                    LoadModeContent(isInitialShow: false);
                };
                synWrap.Children.Add(chip);
            }
            _bodyStack.Children.Add(synWrap);
        }

        _primaryCopyText = copySb.ToString().Trim();
        BuildFooterActions(canCopy: true, canReplace: false, webUrl, pal, m);
    }

    private void RenderTranslationResult(TranslationLookupResult res, ThemePalette pal, FlyoutMetrics m)
    {
        _bodyStack.Children.Clear();
        string rawTrim = _rawText.Trim();
        string lang = _cfg.TranslateTargetLang ?? "en";
        string webUrl = BuildTranslateWebUrl(rawTrim, lang);

        if (!res.Success)
        {
            _primaryCopyText = "";
            _bodyStack.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(9, 8, 9, 8),
                Background = new SolidColorBrush(pal.Surface),
                BorderBrush = new SolidColorBrush(pal.Separator),
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = res.ErrorMessage,
                    FontFamily = W11WindowHelper.TextFont,
                    FontSize = m.CardBodyFontSize,
                    Foreground = new SolidColorBrush(pal.SubText),
                    TextWrapping = TextWrapping.Wrap
                }
            });
            BuildFooterActions(canCopy: false, canReplace: false, webUrl, pal, m);
            return;
        }

        _primaryCopyText = res.TranslatedText;

        // Route + Original preview pill
        var routeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 5) };
        routeRow.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1.5, 6, 1.5),
            Background = new SolidColorBrush(pal.AccentBgTop),
            BorderBrush = new SolidColorBrush(pal.AccentRim),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 7, 0),
            Child = new TextBlock
            {
                Text = $"{res.SourceLang} → {res.TargetLang.ToUpperInvariant()}",
                FontFamily = W11WindowHelper.TextFont,
                FontWeight = FontWeights.SemiBold,
                FontSize = m.CardSubFontSize - 0.5,
                Foreground = new SolidColorBrush(pal.AccentText)
            }
        });
        routeRow.Children.Add(new TextBlock
        {
            Text = ShortSnippet(rawTrim, 34),
            FontFamily = W11WindowHelper.TextFont,
            FontSize = m.CardSubFontSize,
            Foreground = new SolidColorBrush(pal.SubText),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        _bodyStack.Children.Add(routeRow);

        // Primary translation surface card
        var transStack = new StackPanel { Orientation = Orientation.Vertical };
        transStack.Children.Add(new TextBlock
        {
            Text = res.TranslatedText,
            FontFamily = W11WindowHelper.TextFont,
            FontWeight = FontWeights.SemiBold,
            FontSize = m.CardTitleFontSize,
            Foreground = new SolidColorBrush(pal.Text),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = m.CardTitleFontSize * 1.32
        });

        if (!string.IsNullOrEmpty(res.Transliteration))
        {
            transStack.Children.Add(new TextBlock
            {
                Text = res.Transliteration,
                FontFamily = W11WindowHelper.TextFont,
                FontStyle = FontStyles.Italic,
                FontSize = m.CardSubFontSize,
                Foreground = new SolidColorBrush(pal.SubText),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0)
            });
        }

        _bodyStack.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(9, 7, 9, 7),
            Background = new SolidColorBrush(pal.Surface),
            BorderBrush = new SolidColorBrush(pal.Separator),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 6),
            Child = transStack
        });

        foreach (var (pos, terms) in res.Alternatives)
        {
            var altRow = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 2) };
            if (!string.IsNullOrEmpty(pos))
            {
                altRow.Children.Add(new TextBlock
                {
                    Text = pos + ": ",
                    FontFamily = W11WindowHelper.TextFont,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = m.CardSubFontSize,
                    Foreground = new SolidColorBrush(pal.SubText),
                    Margin = new Thickness(0, 0, 4, 0)
                });
            }
            altRow.Children.Add(new TextBlock
            {
                Text = string.Join(" · ", terms),
                FontFamily = W11WindowHelper.TextFont,
                FontSize = m.CardSubFontSize,
                Foreground = new SolidColorBrush(pal.Text),
                TextWrapping = TextWrapping.Wrap
            });
            _bodyStack.Children.Add(altRow);
        }

        BuildFooterActions(canCopy: true, canReplace: _editable, webUrl, pal, m);
    }

    private void RenderInspectBody(ThemePalette pal, FlyoutMetrics m)
    {
        _bodyStack.Children.Clear();
        string raw = _rawText ?? "";
        SmartParser.GetTextStats(raw, out int words, out int chars, out int lines);
        int noSpaceChars = raw.Count(c => !char.IsWhiteSpace(c));
        int utf8Bytes = Encoding.UTF8.GetByteCount(raw);
        int readSec = Math.Max(1, (int)Math.Ceiling(words / 3.3));
        string readLabel = readSec >= 60 ? $"{readSec / 60}m {readSec % 60}s" : $"{readSec}s";

        _primaryCopyText = $"{words} words, {chars} characters ({noSpaceChars} no spaces), {lines} lines, {utf8Bytes} UTF-8 bytes";

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        (string Label, string Val)[] stats =
        {
            ("Words", words.ToString()),
            ("Characters", chars.ToString()),
            ("No Spaces", noSpaceChars.ToString()),
            ("Lines", lines.ToString()),
            ("Read Time", readLabel),
            ("UTF-8 Bytes", utf8Bytes.ToString())
        };

        for (int i = 0; i < stats.Length; i++)
        {
            var tile = new Border
            {
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(7, 5, 7, 5),
                Margin = new Thickness(2),
                Background = new SolidColorBrush(pal.Surface),
                BorderBrush = new SolidColorBrush(pal.Separator),
                BorderThickness = new Thickness(1),
                Child = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = stats[i].Val,
                            FontFamily = W11WindowHelper.DisplayFont,
                            FontWeight = FontWeights.SemiBold,
                            FontSize = m.CardTitleFontSize - 0.5,
                            Foreground = new SolidColorBrush(pal.AccentText)
                        },
                        new TextBlock
                        {
                            Text = stats[i].Label,
                            FontFamily = W11WindowHelper.TextFont,
                            FontSize = m.CardSubFontSize - 0.5,
                            Foreground = new SolidColorBrush(pal.SubText)
                        }
                    }
                }
            };
            Grid.SetRow(tile, i / 3);
            Grid.SetColumn(tile, i % 3);
            grid.Children.Add(tile);
        }
        _bodyStack.Children.Add(grid);

        string urlEnc = Uri.EscapeDataString(raw.Trim());
        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));

        void AddQuickCopyRow(string label, string value)
        {
            string valCopy = value;
            var row = new Border
            {
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(7, 4, 7, 4),
                Margin = new Thickness(0, 2, 0, 2),
                Background = new SolidColorBrush(pal.PillFill),
                BorderBrush = new SolidColorBrush(pal.PillBorder),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Child = new DockPanel
                {
                    Children =
                    {
                        new TextBlock
                        {
                            Text = label,
                            FontFamily = W11WindowHelper.TextFont,
                            FontWeight = FontWeights.SemiBold,
                            FontSize = m.CardSubFontSize,
                            Foreground = new SolidColorBrush(pal.SubText),
                            Width = 66,
                            VerticalAlignment = VerticalAlignment.Center
                        },
                        new TextBlock
                        {
                            Text = ShortSnippet(valCopy, 32),
                            FontFamily = W11WindowHelper.TextFont,
                            FontSize = m.CardSubFontSize,
                            Foreground = new SolidColorBrush(pal.Text),
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            VerticalAlignment = VerticalAlignment.Center
                        }
                    }
                }
            };
            row.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                HideCard();
                _onCopyText?.Invoke(valCopy);
                _onToast?.Invoke($"Copied {label}");
            };
            _bodyStack.Children.Add(row);
        }

        AddQuickCopyRow("URL Encoded", urlEnc);
        AddQuickCopyRow("Base64", b64);

        BuildFooterActions(canCopy: true, canReplace: false, webUrl: null, pal, m);
    }

    private void BuildFooterActions(bool canCopy, bool canReplace, string webUrl, ThemePalette pal, FlyoutMetrics m)
    {
        _footerRow.Children.Clear();
        _hintMap.Clear();

        int hintNum = 1;

        if (canCopy && !string.IsNullOrEmpty(_primaryCopyText))
        {
            string hint = SmartParser.HintForIndex(hintNum++);
            void CopyAction()
            {
                string toCopy = _primaryCopyText;
                HideCard();
                _onCopyText?.Invoke(toCopy);
                _onToast?.Invoke(_mode == ResultCardMode.Translate ? "Translation copied" :
                                 _mode == ResultCardMode.Define ? "Definition copied" : "Stats copied");
            }
            _hintMap[hint] = CopyAction;
            _footerRow.Children.Add(W11WindowHelper.CreateActionPillButton(
                _cfg, "\uE8C8", "Copy", hint, isPrimary: true, pal, m, CopyAction));
        }

        if (canReplace && !string.IsNullOrEmpty(_primaryCopyText))
        {
            string hint = SmartParser.HintForIndex(hintNum++);
            void ReplaceAction()
            {
                string toReplace = _primaryCopyText;
                IntPtr win = _targetWin;
                HideCard();
                _onReplaceText?.Invoke(win, toReplace);
            }
            _hintMap[hint] = ReplaceAction;
            _footerRow.Children.Add(W11WindowHelper.CreateActionPillButton(
                _cfg, "\uE8AC", "Replace", hint, isPrimary: false, pal, m, ReplaceAction));
        }

        if (!string.IsNullOrEmpty(webUrl))
        {
            string hint = SmartParser.HintForIndex(hintNum++);
            string capturedUrl = webUrl;
            void OpenWebAction()
            {
                HideCard();
                _onOpenWebUrl?.Invoke(capturedUrl);
            }
            _hintMap[hint] = OpenWebAction;
            _footerRow.Children.Add(W11WindowHelper.CreateActionPillButton(
                _cfg, "\uE8A7", "Web", hint, isPrimary: false, pal, m, OpenWebAction));
        }

        _footerSep.Visibility = _footerRow.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _footerRow.Visibility = _footerRow.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PresentCardWindow(bool isInitialShow)
    {
        if (!IsVisible)
            Show();
        UpdateLayout();

        if (isInitialShow || !_isOpen)
        {
            W11WindowHelper.PositionFlyoutNearPoint(Hwnd, this, _anchorX, _anchorY, preferAbove: false);
            _isOpen = true;
            W11WindowHelper.AnimateFlyoutIn(this, _card, _cfg.FadeInMs, W11WindowHelper.ShouldAnimate(_cfg));
        }
        else
        {
            ClampBottomAfterResize();
        }
    }

    private void ClampBottomAfterResize()
    {
        UpdateLayout();
        if (Hwnd == IntPtr.Zero) return;
        double scale = Native.ScaleAt(_anchorX, _anchorY);
        var work = Native.WorkAreaAt(_anchorX, _anchorY, out _);
        int physH = (int)Math.Round(ActualHeight * scale);
        int pad = (int)Math.Round(6 * scale);
        if (Native.GetWindowRect(Hwnd, out var rect))
        {
            int py = rect.Top;
            if (py + physH > work.Bottom - pad)
            {
                py = Math.Max(work.Top + pad, work.Bottom - physH - pad);
                Native.MoveWindowPhysical(Hwnd, rect.Left, py);
            }
        }
    }

    private static string BuildTranslateWebUrl(string text, string targetLang)
    {
        string tl = string.IsNullOrWhiteSpace(targetLang) ? "en" : targetLang.Trim();
        return $"https://translate.google.com/?sl=auto&tl={Uri.EscapeDataString(tl)}&op=translate&text={Uri.EscapeDataString(text)}";
    }

    private static string ShortSnippet(string s, int maxLen)
    {
        string flat = System.Text.RegularExpressions.Regex.Replace(s ?? "", @"\s+", " ").Trim();
        return flat.Length <= maxLen ? flat : flat[..(maxLen - 1)] + "…";
    }

    private void CancelPendingLookup()
    {
        if (_cts != null)
        {
            try { _cts.Cancel(); _cts.Dispose(); } catch { }
            _cts = null;
        }
    }

    public void HideCard()
    {
        CancelPendingLookup();
        _langPickerExpanded = false;
        if (!_isOpen && !IsVisible) return;
        _isOpen = false;
        _card.BeginAnimation(UIElement.OpacityProperty, null);
        Hide();
    }
}

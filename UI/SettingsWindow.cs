using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace SparkBar;

public sealed class ActionRowModel : INotifyPropertyChanged
{
    private bool _enabled;
    private string _displayMode = "icon";

    public string Action { get; init; } = "";
    public string Notes { get; init; } = "";

    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled))); }
    }

    public string DisplayMode
    {
        get => _displayMode;
        set { _displayMode = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayMode))); }
    }

    public event PropertyChangedEventHandler PropertyChanged;
}

public sealed class EngineRowModel : INotifyPropertyChanged
{
    private string _name = "";
    private string _url = "";
    private bool _pin;
    private bool _isDefault;

    public string GlyphHex { get; set; } = "";

    public string Name
    {
        get => _name;
        set { _name = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name))); }
    }

    public string Url
    {
        get => _url;
        set { _url = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Url))); }
    }

    public bool Pin
    {
        get => _pin;
        set
        {
            _pin = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Pin)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PinText)));
        }
    }

    public string PinText => _pin ? "Pinned" : "Menu";

    public bool IsDefault
    {
        get => _isDefault;
        set
        {
            _isDefault = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDefault)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RoleText)));
        }
    }

    public string RoleText => _isDefault ? "★ Default" : "";

    public event PropertyChangedEventHandler PropertyChanged;
}

internal sealed class SettingsWindow : Window
{
    private readonly ConfigService _cfg;
    private readonly Action _onSettingsChanged;
    private readonly ObservableCollection<ActionRowModel> _actionRows = new();
    private readonly ObservableCollection<EngineRowModel> _engineRows = new();
    private readonly ObservableCollection<string> _excludedRows = new();

    private ComboBox _themeCombo, _sizeCombo;
    private CheckBox _twoLayerChk, _startupChk, _animChk, _hintsChk, _kbdChk, _fsChk, _fbChk, _uncChk;
    private TextBox _timeoutEd, _dragEd;
    private ComboBox _defaultSearchCombo;
    private ListBox _actListBox, _engListBox, _exclListBox;
    private TextBlock _statusText;
    private string _selectedDefaultEngine;
    private bool _updatingDefaultCombo;
    private Action<int> _selectNavTab;

    public SettingsWindow(ConfigService cfg, Action onSettingsChanged)
    {
        _cfg = cfg;
        _onSettingsChanged = onSettingsChanged;
        _selectedDefaultEngine = cfg.DefaultSearchEngine;

        var pal = cfg.UpdatePalette();
        var textBrush = new SolidColorBrush(pal.Text);

        Title = "SparkBar — Settings";
        Width = 780;
        Height = 590;
        MinWidth = 720;
        MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Foreground = textBrush;
        TextElement.SetForeground(this, textBrush);

        PopulateModels();
        BuildLayout(pal);

        SourceInitialized += (_, _) => W11WindowHelper.ApplyMicaWindowTheme(this, pal, useAcrylic: false);
        Closed += (_, _) => Native.TrimWorkingSet();
    }

    public void SelectTab(int index) => _selectNavTab?.Invoke(index);

    private void PopulateModels()
    {
        _actionRows.Clear();
        foreach (string act in ConfigService.ActionCatalog)
        {
            _actionRows.Add(new ActionRowModel
            {
                Action = act,
                Enabled = !_cfg.ActionEnabled.TryGetValue(act, out bool en) || en,
                DisplayMode = _cfg.ActionDisplay.TryGetValue(act, out string dm) ? dm : "icon",
                Notes = ConfigService.ActionNotes.TryGetValue(act, out string n) ? n : ""
            });
        }

        _engineRows.Clear();
        foreach (var e in _cfg.SearchEngines)
        {
            _engineRows.Add(new EngineRowModel
            {
                Name = e.Name,
                Url = e.Url,
                Pin = e.Pin,
                GlyphHex = e.GlyphHex,
                IsDefault = string.Equals(e.Name, _selectedDefaultEngine, StringComparison.OrdinalIgnoreCase)
            });
        }

        RefreshExcludedList();
    }

    private void RefreshExcludedList()
    {
        _excludedRows.Clear();
        foreach (string exe in _cfg.ExcludedExes.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            _excludedRows.Add(exe);
    }

    public void RefreshExternalChanges()
    {
        _selectedDefaultEngine = _cfg.DefaultSearchEngine;
        RefreshDefaultEngineIndicators();
        RefreshExcludedList();
    }

    private void BuildLayout(ThemePalette pal)
    {
        var textBrush = new SolidColorBrush(pal.Text);
        var subBrush = new SolidColorBrush(pal.SubText);

        var root = new Grid { Margin = new Thickness(16, 12, 16, 14) };
        TextElement.SetForeground(root, textBrush);
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var mainGrid = new Grid();
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(192) });
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var navStack = new StackPanel { Orientation = Orientation.Vertical };

        var brandRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4, 8, 16) };
        brandRow.Children.Add(new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(8),
            Background = new LinearGradientBrush(Color.FromRgb(0x4F, 0x46, 0xE5), Color.FromRgb(0x06, 0xB6, 0xD4), 45),
            Margin = new Thickness(0, 0, 10, 0),
            Child = new TextBlock
            {
                Text = "✦",
                FontWeight = FontWeights.Bold,
                FontSize = 15,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        });
        var brandTxt = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        brandTxt.Children.Add(new TextBlock
        {
            Text = "SparkBar",
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            Foreground = textBrush
        });
        brandTxt.Children.Add(new TextBlock
        {
            Text = "Windows 11 Edition",
            FontSize = 11,
            Foreground = subBrush
        });
        brandRow.Children.Add(brandTxt);
        navStack.Children.Add(brandRow);

        var pageHost = new Grid();
        var pages = new UIElement[]
        {
            BuildGeneralPage(pal),
            BuildActionsPage(pal),
            BuildSearchEnginesPage(pal),
            BuildExcludedAppsPage(pal)
        };
        foreach (var p in pages)
        {
            p.Visibility = Visibility.Collapsed;
            pageHost.Children.Add(p);
        }

        var navButtons = new List<(Border Container, Border Indicator, int Index)>();
        void SelectNav(int idx)
        {
            for (int i = 0; i < pages.Length; i++)
                pages[i].Visibility = i == idx ? Visibility.Visible : Visibility.Collapsed;
            foreach (var (cont, ind, bIdx) in navButtons)
            {
                bool active = bIdx == idx;
                cont.Background = active ? new SolidColorBrush(pal.Surface) : Brushes.Transparent;
                cont.BorderBrush = active ? new SolidColorBrush(pal.Separator) : Brushes.Transparent;
                ind.Visibility = active ? Visibility.Visible : Visibility.Hidden;
            }
        }
        _selectNavTab = SelectNav;

        (string Icon, string Title)[] navItems =
        {
            ("\uE713", "General"),
            ("\uE8FD", "Toolbar Actions"),
            ("\uE721", "Search & URLs"),
            ("\uE8F8", "Excluded Apps")
        };

        for (int i = 0; i < navItems.Length; i++)
        {
            int captureIdx = i;
            var row = new Grid { Height = 36 };
            var ind = new Border
            {
                Width = 3,
                Height = 16,
                CornerRadius = new CornerRadius(1.5),
                Background = new SolidColorBrush(pal.Accent),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 0, 0),
                Visibility = Visibility.Hidden
            };
            var sp = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 8, 0)
            };
            sp.Children.Add(new TextBlock
            {
                Text = navItems[i].Icon,
                FontFamily = new FontFamily(_cfg.IconFontFamily),
                FontSize = 14,
                Foreground = textBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            });
            sp.Children.Add(new TextBlock
            {
                Text = navItems[i].Title,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = textBrush,
                VerticalAlignment = VerticalAlignment.Center
            });
            row.Children.Add(ind);
            row.Children.Add(sp);

            var navBorder = new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 2, 0, 2),
                Cursor = Cursors.Hand,
                Child = row
            };
            navBorder.MouseLeftButtonUp += (_, _) => SelectNav(captureIdx);
            navButtons.Add((navBorder, ind, captureIdx));
            navStack.Children.Add(navBorder);
        }

        SelectNav(0);

        Grid.SetColumn(navStack, 0);
        mainGrid.Children.Add(navStack);

        var contentCard = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb((byte)(pal.IsLight ? 160 : 120), pal.Surface.R, pal.Surface.G, pal.Surface.B)),
            BorderBrush = new SolidColorBrush(pal.Separator),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(18, 14, 18, 14),
            Child = pageHost
        };
        Grid.SetColumn(contentCard, 2);
        mainGrid.Children.Add(contentCard);

        Grid.SetRow(mainGrid, 0);
        root.Children.Add(mainGrid);

        var footer = new DockPanel { Margin = new Thickness(4, 12, 0, 0) };
        _statusText = new TextBlock
        {
            Text = "Shortcuts: Ctrl+Alt+P (popup)  ·  Ctrl+Alt+V (clip history)  ·  Ctrl+Alt+X (exclude app)",
            FontSize = 12,
            Foreground = subBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(_statusText, Dock.Left);
        footer.Children.Add(_statusText);

        var footerBtns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var applyBtn = new Button { Content = "Apply", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
        applyBtn.Click += (_, _) => CommitSettings(closeAfter: false);

        var saveCloseBtn = new Button
        {
            Content = "Save & Close",
            IsDefault = true,
            Padding = new Thickness(16, 6, 16, 6),
            Margin = new Thickness(0, 0, 8, 0),
            FontWeight = FontWeights.SemiBold
        };
        try { if (Application.Current?.TryFindResource("AccentButtonStyle") is Style s) saveCloseBtn.Style = s; } catch { }
        saveCloseBtn.Click += (_, _) => CommitSettings(closeAfter: true);

        var cancelBtn = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(14, 6, 14, 6) };
        cancelBtn.Click += (_, _) => Close();

        footerBtns.Children.Add(applyBtn);
        footerBtns.Children.Add(saveCloseBtn);
        footerBtns.Children.Add(cancelBtn);
        DockPanel.SetDock(footerBtns, Dock.Right);
        footer.Children.Add(footerBtns);

        Grid.SetRow(footer, 1);
        root.Children.Add(footer);

        Content = root;
    }

    private UIElement BuildGeneralPage(ThemePalette pal)
    {
        var textBrush = new SolidColorBrush(pal.Text);
        var sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var sp = new StackPanel { Orientation = Orientation.Vertical };

        sp.Children.Add(MakeSectionTitle("Appearance & Theme", pal));

        var themeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 6) };
        themeRow.Children.Add(new TextBlock
        {
            Text = "Color theme:",
            Foreground = textBrush,
            Width = 95,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13
        });
        _themeCombo = new ComboBox { Width = 140, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 22, 0) };
        foreach (string tm in new[] { "midnight", "slate", "auto", "dark", "light" })
            _themeCombo.Items.Add(tm);
        _themeCombo.SelectedItem = _cfg.ThemeMode;
        themeRow.Children.Add(_themeCombo);

        themeRow.Children.Add(new TextBlock
        {
            Text = "Popup bar size:",
            Foreground = textBrush,
            Width = 105,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13
        });
        _sizeCombo = new ComboBox { Width = 130, Padding = new Thickness(8, 4, 8, 4) };
        foreach (string sz in new[] { "mini", "compact", "normal", "large" })
            _sizeCombo.Items.Add(sz);
        _sizeCombo.SelectedItem = _cfg.PopupSize;
        themeRow.Children.Add(_sizeCombo);
        sp.Children.Add(themeRow);

        _twoLayerChk = MakeCheck("Two-layer smart popup (relevant items on lower row near cursor, utilities on upper row)", _cfg.TwoLayerPopup, textBrush);
        _startupChk = MakeCheck("Launch SparkBar automatically when Windows starts", _cfg.IsRunAtStartupEnabled(), textBrush);
        _animChk  = MakeCheck("Smooth fade & slide animations", _cfg.AnimationsEnabled, textBrush);
        _hintsChk = MakeCheck("Show keyboard shortcut badges (1-9, A-Z) on toolbar", _cfg.ShowHintBadges, textBrush);
        sp.Children.Add(_twoLayerChk);
        sp.Children.Add(_startupChk);
        sp.Children.Add(_animChk);
        sp.Children.Add(_hintsChk);

        sp.Children.Add(MakeSectionTitle("Selection & Trigger Behavior", pal, topMargin: 10));
        _kbdChk = MakeCheck("Trigger popup after selecting text with Shift + Arrow keys", _cfg.KeyboardTriggerEnabled, textBrush);
        _fsChk  = MakeCheck("Pause automatically when a fullscreen game or video is active", _cfg.PauseInFullscreen, textBrush);
        _fbChk  = MakeCheck("Use safe Ctrl+C fallback in apps without UI Automation support", _cfg.UseCopyFallback, textBrush);
        _uncChk = MakeCheck("Offer Open / Show in folder for network paths (\\\\server\\share)", _cfg.AllowUncPaths, textBrush);
        sp.Children.Add(_kbdChk);
        sp.Children.Add(_fsChk);
        sp.Children.Add(_fbChk);
        sp.Children.Add(_uncChk);

        var numRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 2) };
        numRow.Children.Add(new TextBlock { Text = "Auto-hide timeout (ms):", Foreground = textBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        _timeoutEd = new TextBox { Text = _cfg.PopupTimeoutMs.ToString(), Width = 78, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 0, 20, 0) };
        numRow.Children.Add(_timeoutEd);
        numRow.Children.Add(new TextBlock { Text = "Min drag distance (px):", Foreground = textBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        _dragEd = new TextBox { Text = _cfg.MinDragPx.ToString(), Width = 62, Padding = new Thickness(6, 4, 6, 4) };
        numRow.Children.Add(_dragEd);
        sp.Children.Add(numRow);

        sp.Children.Add(MakeSectionTitle("Backup & Maintenance", pal, topMargin: 12));
        var maintRow = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };

        Button MakeMaintBtn(string text, Action onClick)
        {
            var b = new Button { Content = text, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 6) };
            b.Click += (_, _) => onClick();
            return b;
        }

        maintRow.Children.Add(MakeMaintBtn("Export backup...", ExportBackup));
        maintRow.Children.Add(MakeMaintBtn("Import backup...", ImportBackup));
        maintRow.Children.Add(MakeMaintBtn("Reset toolbar order", () =>
        {
            _cfg.PopupOrder.Clear();
            try { if (File.Exists(_cfg.PopupOrderFile)) File.Delete(_cfg.PopupOrderFile); } catch { }
            _statusText.Text = "Toolbar order restored to default.";
        }));
        maintRow.Children.Add(MakeMaintBtn("Factory reset all...", FactoryResetAll));
        sp.Children.Add(maintRow);

        sv.Content = sp;
        return sv;
    }

    private UIElement BuildActionsPage(ThemePalette pal)
    {
        var textBrush = new SolidColorBrush(pal.Text);
        var subBrush = new SolidColorBrush(pal.SubText);
        var accentBrush = new SolidColorBrush(pal.AccentText);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        grid.Children.Add(new TextBlock
        {
            Text = "Check actions to show on the toolbar. Double-click a row (or click Toggle Icon / Text) to switch display mode.",
            FontSize = 12,
            Foreground = subBrush,
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap
        });

        var colHdr = new Grid { Margin = new Thickness(8, 0, 18, 4) };
        colHdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        colHdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(155) });
        colHdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
        colHdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        colHdr.Children.Add(new TextBlock { Text = "On", FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = subBrush });
        var hAct = new TextBlock { Text = "Action", FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = subBrush };
        Grid.SetColumn(hAct, 1); colHdr.Children.Add(hAct);
        var hDisp = new TextBlock { Text = "Mode", FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = subBrush };
        Grid.SetColumn(hDisp, 2); colHdr.Children.Add(hDisp);
        var hNotes = new TextBlock { Text = "Description", FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = subBrush };
        Grid.SetColumn(hNotes, 3); colHdr.Children.Add(hNotes);
        Grid.SetRow(colHdr, 1);
        grid.Children.Add(colHdr);

        _actListBox = new ListBox
        {
            ItemsSource = _actionRows,
            SelectionMode = SelectionMode.Single,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ItemContainerStyle = MakeCompactListBoxItemStyle()
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_actListBox, ScrollBarVisibility.Disabled);

        var rowGrid = new FrameworkElementFactory(typeof(Grid));
        rowGrid.SetValue(Grid.MarginProperty, new Thickness(2, 1, 2, 1));
        var c0 = new FrameworkElementFactory(typeof(ColumnDefinition)); c0.SetValue(ColumnDefinition.WidthProperty, new GridLength(34));
        var c1 = new FrameworkElementFactory(typeof(ColumnDefinition)); c1.SetValue(ColumnDefinition.WidthProperty, new GridLength(155));
        var c2 = new FrameworkElementFactory(typeof(ColumnDefinition)); c2.SetValue(ColumnDefinition.WidthProperty, new GridLength(68));
        var c3 = new FrameworkElementFactory(typeof(ColumnDefinition)); c3.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
        rowGrid.AppendChild(c0); rowGrid.AppendChild(c1); rowGrid.AppendChild(c2); rowGrid.AppendChild(c3);

        var chk = new FrameworkElementFactory(typeof(CheckBox));
        chk.SetBinding(CheckBox.IsCheckedProperty, new System.Windows.Data.Binding("Enabled") { Mode = System.Windows.Data.BindingMode.TwoWay });
        chk.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        chk.SetValue(Grid.ColumnProperty, 0);
        rowGrid.AppendChild(chk);

        var actTxt = new FrameworkElementFactory(typeof(TextBlock));
        actTxt.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Action"));
        actTxt.SetValue(TextBlock.ForegroundProperty, textBrush);
        actTxt.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        actTxt.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        actTxt.SetValue(Grid.ColumnProperty, 1);
        rowGrid.AppendChild(actTxt);

        var dispTxt = new FrameworkElementFactory(typeof(TextBlock));
        dispTxt.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("DisplayMode"));
        dispTxt.SetValue(TextBlock.ForegroundProperty, accentBrush);
        dispTxt.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        dispTxt.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        dispTxt.SetValue(Grid.ColumnProperty, 2);
        rowGrid.AppendChild(dispTxt);

        var noteTxt = new FrameworkElementFactory(typeof(TextBlock));
        noteTxt.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Notes"));
        noteTxt.SetValue(TextBlock.ForegroundProperty, subBrush);
        noteTxt.SetValue(TextBlock.FontSizeProperty, 11.5);
        noteTxt.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        noteTxt.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        noteTxt.SetValue(Grid.ColumnProperty, 3);
        rowGrid.AppendChild(noteTxt);

        _actListBox.ItemTemplate = new DataTemplate { VisualTree = rowGrid };

        void ToggleSelectedActionDisplay()
        {
            if (_actListBox.SelectedItem is ActionRowModel row)
                row.DisplayMode = row.DisplayMode == "icon" ? "text" : "icon";
        }
        _actListBox.MouseDoubleClick += (_, _) => ToggleSelectedActionDisplay();

        Grid.SetRow(_actListBox, 2);
        grid.Children.Add(_actListBox);

        var bar = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var leftBtns = new StackPanel { Orientation = Orientation.Horizontal };
        var togBtn = new Button { Content = "Toggle Icon / Text", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
        togBtn.Click += (_, _) => ToggleSelectedActionDisplay();
        var enAllBtn = new Button { Content = "Enable All", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
        enAllBtn.Click += (_, _) => { foreach (var r in _actionRows) r.Enabled = true; };
        var disAllBtn = new Button { Content = "Disable All", Padding = new Thickness(12, 5, 12, 5) };
        disAllBtn.Click += (_, _) => { foreach (var r in _actionRows) r.Enabled = false; };
        leftBtns.Children.Add(togBtn);
        leftBtns.Children.Add(enAllBtn);
        leftBtns.Children.Add(disAllBtn);
        DockPanel.SetDock(leftBtns, Dock.Left);
        bar.Children.Add(leftBtns);

        bar.Children.Add(new TextBlock
        {
            Text = "Tip: Drag popup buttons to reorder",
            FontSize = 11,
            Foreground = subBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        });

        Grid.SetRow(bar, 3);
        grid.Children.Add(bar);
        return grid;
    }

    private UIElement BuildSearchEnginesPage(ThemePalette pal)
    {
        var textBrush = new SolidColorBrush(pal.Text);
        var subBrush = new SolidColorBrush(pal.SubText);
        var accentBrush = new SolidColorBrush(pal.AccentText);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var topBar = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        topBar.Children.Add(new TextBlock
        {
            Text = "Default Search:",
            Foreground = textBrush,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 8, 0)
        });
        _defaultSearchCombo = new ComboBox { Width = 170, Padding = new Thickness(8, 4, 8, 4) };
        _defaultSearchCombo.SelectionChanged += (_, _) =>
        {
            if (_updatingDefaultCombo) return;
            if (_defaultSearchCombo.SelectedItem is string s && !string.IsNullOrEmpty(s))
            {
                _selectedDefaultEngine = s;
                RefreshDefaultEngineIndicators();
            }
        };
        topBar.Children.Add(_defaultSearchCombo);

        var refFavBtn = new Button
        {
            Content = "Refresh Favicons",
            Padding = new Thickness(12, 4, 12, 4),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        refFavBtn.Click += (_, _) =>
        {
            _cfg.ClearFaviconCache();
            _statusText.Text = "Icon cache cleared. Downloading fresh favicons in the background...";
        };
        DockPanel.SetDock(refFavBtn, Dock.Right);
        topBar.Children.Add(refFavBtn);
        Grid.SetRow(topBar, 0);
        grid.Children.Add(topBar);

        var hint = new TextBlock
        {
            Text = "Double-click any row to make it Default Search. Use {selection} anywhere in the URL, or leave a URL prefix.",
            FontSize = 11.5,
            Foreground = subBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6)
        };
        Grid.SetRow(hint, 1);
        grid.Children.Add(hint);

        _engListBox = new ListBox
        {
            ItemsSource = _engineRows,
            SelectionMode = SelectionMode.Single,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ItemContainerStyle = MakeCompactListBoxItemStyle()
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_engListBox, ScrollBarVisibility.Disabled);

        var rowGrid = new FrameworkElementFactory(typeof(Grid));
        rowGrid.SetValue(Grid.MarginProperty, new Thickness(2, 2, 2, 2));
        var ec0 = new FrameworkElementFactory(typeof(ColumnDefinition)); ec0.SetValue(ColumnDefinition.WidthProperty, new GridLength(110));
        var ec1 = new FrameworkElementFactory(typeof(ColumnDefinition)); ec1.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
        var ec2 = new FrameworkElementFactory(typeof(ColumnDefinition)); ec2.SetValue(ColumnDefinition.WidthProperty, new GridLength(60));
        var ec3 = new FrameworkElementFactory(typeof(ColumnDefinition)); ec3.SetValue(ColumnDefinition.WidthProperty, new GridLength(75));
        rowGrid.AppendChild(ec0); rowGrid.AppendChild(ec1); rowGrid.AppendChild(ec2); rowGrid.AppendChild(ec3);

        var nameTxt = new FrameworkElementFactory(typeof(TextBlock));
        nameTxt.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Name"));
        nameTxt.SetValue(TextBlock.ForegroundProperty, textBrush);
        nameTxt.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        nameTxt.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        nameTxt.SetValue(Grid.ColumnProperty, 0);
        rowGrid.AppendChild(nameTxt);

        var urlTxt = new FrameworkElementFactory(typeof(TextBlock));
        urlTxt.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Url"));
        urlTxt.SetValue(TextBlock.ForegroundProperty, subBrush);
        urlTxt.SetValue(TextBlock.FontSizeProperty, 11.5);
        urlTxt.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        urlTxt.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        urlTxt.SetValue(TextBlock.MarginProperty, new Thickness(0, 0, 8, 0));
        urlTxt.SetValue(Grid.ColumnProperty, 1);
        rowGrid.AppendChild(urlTxt);

        var pinTxt = new FrameworkElementFactory(typeof(TextBlock));
        pinTxt.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("PinText"));
        pinTxt.SetValue(TextBlock.ForegroundProperty, textBrush);
        pinTxt.SetValue(TextBlock.FontSizeProperty, 11.5);
        pinTxt.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        pinTxt.SetValue(Grid.ColumnProperty, 2);
        rowGrid.AppendChild(pinTxt);

        var roleTxt = new FrameworkElementFactory(typeof(TextBlock));
        roleTxt.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("RoleText"));
        roleTxt.SetValue(TextBlock.ForegroundProperty, accentBrush);
        roleTxt.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        roleTxt.SetValue(TextBlock.FontSizeProperty, 11.5);
        roleTxt.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        roleTxt.SetValue(Grid.ColumnProperty, 3);
        rowGrid.AppendChild(roleTxt);

        _engListBox.ItemTemplate = new DataTemplate { VisualTree = rowGrid };
        Grid.SetRow(_engListBox, 2);
        grid.Children.Add(_engListBox);

        var editRow = new Grid { Margin = new Thickness(0, 8, 0, 6) };
        editRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(115) });
        editRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        editRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        editRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var engNameEd = new TextBox { Padding = new Thickness(6, 4, 6, 4), ToolTip = "Engine Name" };
        var engUrlEd  = new TextBox { Padding = new Thickness(6, 4, 6, 4), ToolTip = "URL Prefix or {selection} Template" };
        var chkPanel  = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        var engPinChk = new CheckBox { Content = new TextBlock { Text = "Pinned", Foreground = textBrush }, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        var engDefChk = new CheckBox { Content = new TextBlock { Text = "Default", Foreground = textBrush }, VerticalAlignment = VerticalAlignment.Center };
        chkPanel.Children.Add(engPinChk);
        chkPanel.Children.Add(engDefChk);

        Grid.SetColumn(engNameEd, 0);
        Grid.SetColumn(engUrlEd, 2);
        Grid.SetColumn(chkPanel, 3);
        editRow.Children.Add(engNameEd);
        editRow.Children.Add(engUrlEd);
        editRow.Children.Add(chkPanel);
        Grid.SetRow(editRow, 3);
        grid.Children.Add(editRow);

        _engListBox.SelectionChanged += (_, _) =>
        {
            if (_engListBox.SelectedItem is EngineRowModel r)
            {
                engNameEd.Text = r.Name;
                engUrlEd.Text = r.Url;
                engPinChk.IsChecked = r.Pin;
                engDefChk.IsChecked = r.IsDefault;
            }
        };

        void SetSelectedRowDefault()
        {
            if (_engListBox.SelectedItem is EngineRowModel r)
            {
                _selectedDefaultEngine = r.Name;
                RefreshDefaultEngineIndicators();
                _statusText.Text = "Default search engine set to: " + r.Name;
            }
        }
        _engListBox.MouseDoubleClick += (_, _) => SetSelectedRowDefault();

        var btnRow = new WrapPanel { Orientation = Orientation.Horizontal };
        var addUpdBtn = new Button { Content = "Add / Update", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
        addUpdBtn.Click += (_, _) =>
        {
            string n = Regex.Replace((engNameEd.Text ?? "").Trim(), @"[\t\r\n]+", " ");
            string u = Regex.Replace((engUrlEd.Text ?? "").Trim(), @"[\t\r\n]+", "");
            if (string.IsNullOrEmpty(n) || !SmartParser.IsAllowedActionUri(u))
            {
                MessageBox.Show(this, "Enter a name and a valid URL or app URI (e.g. https://..., obsidian://..., spotify:search:{selection}, es:{selection}).", "SparkBar Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var existing = _engineRows.FirstOrDefault(e => string.Equals(e.Name, n, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.Name = n;
                existing.Url = u;
                existing.Pin = engPinChk.IsChecked == true;
            }
            else
            {
                _engineRows.Add(new EngineRowModel { Name = n, Url = u, Pin = engPinChk.IsChecked == true });
            }
            if (engDefChk.IsChecked == true)
                _selectedDefaultEngine = n;
            RefreshDefaultEngineIndicators();
        };

        var remBtn = new Button { Content = "Remove", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
        remBtn.Click += (_, _) =>
        {
            if (_engListBox.SelectedItem is EngineRowModel r && _engineRows.Count > 1)
            {
                _engineRows.Remove(r);
                RefreshDefaultEngineIndicators();
            }
        };

        var insSelBtn = new Button { Content = "Insert {selection}", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
        insSelBtn.Click += (_, _) =>
        {
            if (!engUrlEd.Text.Contains("{selection}", StringComparison.Ordinal))
                engUrlEd.Text += "{selection}";
        };

        var setDefBtn = new Button { Content = "Set Selected as Default", Padding = new Thickness(12, 5, 12, 5) };
        setDefBtn.Click += (_, _) => SetSelectedRowDefault();

        btnRow.Children.Add(addUpdBtn);
        btnRow.Children.Add(remBtn);
        btnRow.Children.Add(insSelBtn);
        btnRow.Children.Add(setDefBtn);
        Grid.SetRow(btnRow, 4);
        grid.Children.Add(btnRow);

        RefreshDefaultEngineIndicators();
        return grid;
    }

    private void RefreshDefaultEngineIndicators()
    {
        if (_defaultSearchCombo == null || _updatingDefaultCombo) return;
        _updatingDefaultCombo = true;
        try
        {
            if (!_engineRows.Any(e => string.Equals(e.Name, _selectedDefaultEngine, StringComparison.OrdinalIgnoreCase)) && _engineRows.Count > 0)
                _selectedDefaultEngine = _engineRows[0].Name;

            _defaultSearchCombo.Items.Clear();
            foreach (var e in _engineRows)
            {
                e.IsDefault = string.Equals(e.Name, _selectedDefaultEngine, StringComparison.OrdinalIgnoreCase);
                _defaultSearchCombo.Items.Add(e.Name);
            }
            _defaultSearchCombo.SelectedItem = _selectedDefaultEngine;
        }
        finally
        {
            _updatingDefaultCombo = false;
        }
    }

    private UIElement BuildExcludedAppsPage(ThemePalette pal)
    {
        var textBrush = new SolidColorBrush(pal.Text);
        var subBrush = new SolidColorBrush(pal.SubText);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        grid.Children.Add(new TextBlock
        {
            Text = "SparkBar will never activate inside these applications (press Ctrl+Alt+X in any app to toggle instantly):",
            FontSize = 12,
            Foreground = subBrush,
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap
        });

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });

        _exclListBox = new ListBox { ItemsSource = _excludedRows };
        Grid.SetColumn(_exclListBox, 0);
        body.Children.Add(_exclListBox);

        var rightCol = new StackPanel { Orientation = Orientation.Vertical };
        rightCol.Children.Add(new TextBlock { Text = "Add Application", Foreground = textBrush, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 0, 0, 6) });
        rightCol.Children.Add(new TextBlock { Text = "Type process name (e.g. obs64.exe):", FontSize = 11.5, Foreground = subBrush, Margin = new Thickness(0, 0, 0, 4) });

        var manualRow = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var addManualBtn = new Button { Content = "Add", Width = 58, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(6, 0, 0, 0) };
        DockPanel.SetDock(addManualBtn, Dock.Right);
        var manualEd = new TextBox { Padding = new Thickness(6, 4, 6, 4) };
        manualRow.Children.Add(addManualBtn);
        manualRow.Children.Add(manualEd);
        addManualBtn.Click += (_, _) =>
        {
            AddExcludedExe(manualEd.Text);
            manualEd.Text = "";
        };
        rightCol.Children.Add(manualRow);

        rightCol.Children.Add(new TextBlock { Text = "Or pick from running apps:", FontSize = 11.5, Foreground = subBrush, Margin = new Thickness(0, 0, 0, 4) });
        var runRow = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        var addRunBtn = new Button { Content = "Add", Width = 58, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(6, 0, 0, 0) };
        DockPanel.SetDock(addRunBtn, Dock.Right);
        var runCombo = new ComboBox { Padding = new Thickness(6, 4, 6, 4) };
        foreach (string p in GetRunningProcesses())
            runCombo.Items.Add(p);
        if (runCombo.Items.Count > 0) runCombo.SelectedIndex = 0;
        runRow.Children.Add(addRunBtn);
        runRow.Children.Add(runCombo);
        addRunBtn.Click += (_, _) =>
        {
            if (runCombo.SelectedItem is string s)
                AddExcludedExe(s);
        };
        rightCol.Children.Add(runRow);

        var browseBtn = new Button { Content = "Browse for .exe file...", Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 0, 8) };
        browseBtn.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select an application to exclude",
                Filter = "Programs (*.exe)|*.exe"
            };
            if (dlg.ShowDialog() == true)
                AddExcludedExe(dlg.FileName);
        };
        rightCol.Children.Add(browseBtn);

        var remExclBtn = new Button { Content = "Remove Selected App", Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 0, 8) };
        remExclBtn.Click += (_, _) =>
        {
            if (_exclListBox.SelectedItem is string exe)
            {
                _cfg.ExcludedExes.Remove(exe);
                RefreshExcludedList();
                _statusText.Text = "Removed from excluded apps: " + exe;
            }
        };
        rightCol.Children.Add(remExclBtn);

        var restDefBtn = new Button { Content = "Restore Default Exclusions", Padding = new Thickness(10, 6, 10, 6) };
        restDefBtn.Click += (_, _) =>
        {
            _cfg.ExcludedExes = new HashSet<string>(ConfigService.DefaultExcludedExes, StringComparer.OrdinalIgnoreCase);
            RefreshExcludedList();
            _statusText.Text = "Restored default excluded apps.";
        };
        rightCol.Children.Add(restDefBtn);

        Grid.SetColumn(rightCol, 2);
        body.Children.Add(rightCol);

        Grid.SetRow(body, 1);
        grid.Children.Add(body);
        return grid;
    }

    private void AddExcludedExe(string raw)
    {
        string exe = Path.GetFileName((raw ?? "").Trim());
        if (string.IsNullOrEmpty(exe)) return;
        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            exe += ".exe";
        _cfg.ExcludedExes.Add(exe);
        RefreshExcludedList();
        _statusText.Text = "Added to excluded apps: " + exe;
    }

    private List<string> GetRunningProcesses()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            Native.EnumWindows((hwnd, _) =>
            {
                if (Native.IsWindowVisible(hwnd))
                {
                    string exe = Native.ProcessNameOf(hwnd);
                    if (!string.IsNullOrEmpty(exe) && !_cfg.ExcludedExes.Contains(exe))
                        set.Add(exe);
                }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return set.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void CommitSettings(bool closeAfter)
    {
        _cfg.SetRunAtStartup(_startupChk.IsChecked == true);
        _cfg.ThemeMode = (_themeCombo.SelectedItem as string) ?? _cfg.ThemeMode;
        _cfg.PopupSize = (_sizeCombo.SelectedItem as string) ?? _cfg.PopupSize;
        _cfg.TwoLayerPopup = _twoLayerChk.IsChecked == true;
        _cfg.AnimationsEnabled = _animChk.IsChecked == true;
        _cfg.ShowHintBadges = _hintsChk.IsChecked == true;
        _cfg.KeyboardTriggerEnabled = _kbdChk.IsChecked == true;
        _cfg.PauseInFullscreen = _fsChk.IsChecked == true;
        _cfg.UseCopyFallback = _fbChk.IsChecked == true;
        _cfg.AllowUncPaths = _uncChk.IsChecked == true;

        if (int.TryParse(_timeoutEd.Text, out int to) && to >= 500)
            _cfg.PopupTimeoutMs = to;
        if (int.TryParse(_dragEd.Text, out int dp) && dp >= 2)
            _cfg.MinDragPx = dp;

        foreach (var row in _actionRows)
        {
            _cfg.ActionEnabled[row.Action] = row.Enabled;
            _cfg.ActionDisplay[row.Action] = row.DisplayMode;
        }

        if (_engineRows.Count > 0)
        {
            var newEngines = new List<SearchEngineEntry>();
            foreach (var r in _engineRows)
            {
                var entry = new SearchEngineEntry
                {
                    Name = r.Name,
                    Url = r.Url,
                    Pin = r.Pin,
                    GlyphHex = r.GlyphHex
                };
                string fav = _cfg.EnsureFavicon(entry);
                if (!string.IsNullOrEmpty(fav))
                    entry.FaviconPath = fav;
                else
                    _cfg.QueueFavicon(entry);
                newEngines.Add(entry);
            }
            _cfg.SearchEngines = newEngines;
        }

        if (!string.IsNullOrEmpty(_selectedDefaultEngine))
            _cfg.DefaultSearchEngine = _selectedDefaultEngine;

        _cfg.SaveUserSettings();
        _cfg.SaveSearchEngines();
        _cfg.SaveExcludedApps();
        _cfg.UpdatePalette();
        _onSettingsChanged?.Invoke();

        if (closeAfter)
            Close();
        else
            _statusText.Text = "Settings applied and saved.";
    }

    private void ExportBackup()
    {
        var dlg = new OpenFolderDialog { Title = "Choose a folder for the SparkBar backup" };
        if (dlg.ShowDialog() != true) return;
        string folder = dlg.FolderName;
        try
        {
            Directory.CreateDirectory(folder);
            foreach (string src in new[] { _cfg.SettingsFile, _cfg.SearchEnginesFile, _cfg.PopupOrderFile, _cfg.ExcludedAppsFile })
                if (File.Exists(src))
                    File.Copy(src, Path.Combine(folder, Path.GetFileName(src)), true);
            _statusText.Text = "Settings exported to: " + folder;
        }
        catch (Exception ex) { _statusText.Text = "Export failed: " + ex.Message; }
    }

    private void ImportBackup()
    {
        var dlg = new OpenFolderDialog { Title = "Choose the SparkBar backup folder" };
        if (dlg.ShowDialog() != true) return;
        string folder = dlg.FolderName;
        try
        {
            foreach (string file in new[] { "SparkBarSettings.ini", "SearchEngines.txt", "ToolbarOrder.txt", "CustomActions.txt", "ExcludedApps.txt" })
            {
                string src = Path.Combine(folder, file);
                if (File.Exists(src))
                    File.Copy(src, Path.Combine(_cfg.BaseDir, file), true);
            }
            _cfg.ReloadAll();
            PopulateModels();
            _onSettingsChanged?.Invoke();
            _statusText.Text = "Backup imported and loaded.";
        }
        catch (Exception ex) { _statusText.Text = "Import failed: " + ex.Message; }
    }

    private void FactoryResetAll()
    {
        if (MessageBox.Show(this,
                "Reset theme, toolbar order, action settings, search engines, excluded apps, and cached icons?",
                "SparkBar Settings",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        foreach (string f in new[] { _cfg.SettingsFile, _cfg.PopupOrderFile, _cfg.SearchEnginesFile, _cfg.LegacyCustomFile, _cfg.ExcludedAppsFile })
            try { if (File.Exists(f)) File.Delete(f); } catch { }
        try { if (Directory.Exists(_cfg.FaviconCacheDir)) Directory.Delete(_cfg.FaviconCacheDir, true); } catch { }

        _cfg.ReloadAll();
        PopulateModels();
        _onSettingsChanged?.Invoke();
        _statusText.Text = "Factory defaults restored.";
    }

    private static TextBlock MakeSectionTitle(string text, ThemePalette pal, double topMargin = 2)
    {
        return new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            Foreground = new SolidColorBrush(pal.Text),
            Margin = new Thickness(0, topMargin, 0, 5)
        };
    }

    private static CheckBox MakeCheck(string text, bool isChecked, Brush fg)
    {
        return new CheckBox
        {
            Content = new TextBlock { Text = text, Foreground = fg, FontSize = 13 },
            IsChecked = isChecked,
            Padding = new Thickness(6, 1, 0, 1),
            MinHeight = 26,
            Margin = new Thickness(0, 1, 0, 1)
        };
    }

    private static Style MakeCompactListBoxItemStyle()
    {
        Style baseStyle = null;
        try { baseStyle = Application.Current?.TryFindResource(typeof(ListBoxItem)) as Style; } catch { }
        var st = new Style(typeof(ListBoxItem), baseStyle);
        st.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 4, 8, 4)));
        st.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 28.0));
        st.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        return st;
    }
}

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace SparkBar;

internal sealed class UrlBuilderWindow : Window
{
    public UrlBuilderWindow(
        ConfigService cfg,
        string rawUrl,
        Action<string, string, bool, bool> onSave)
    {
        var pal = cfg.UpdatePalette();
        var textBrush = new SolidColorBrush(pal.Text);
        var subBrush = new SolidColorBrush(pal.SubText);

        Title = "Add Search Engine / Web Action";
        Width = 540;
        Height = 315;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Foreground = textBrush;
        TextElement.SetForeground(this, textBrush);

        SmartParser.ParseUrlActionCandidates(rawUrl, out string suggestedName, out var candidates, out string defaultTpl);

        var root = new Grid { Margin = new Thickness(20, 16, 20, 16) };
        TextElement.SetForeground(root, textBrush);
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new TextBlock
        {
            Text = "Add Search Engine / Web Action from URL",
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 16,
            Foreground = textBrush,
            Margin = new Thickness(0, 0, 0, 4)
        };
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var sub = new TextBlock
        {
            Text = "Use {selection} anywhere in the URL, or leave a prefix URL and text will be appended.",
            FontSize = 12,
            Foreground = subBrush,
            Margin = new Thickness(0, 0, 0, 14)
        };
        Grid.SetRow(sub, 1);
        root.Children.Add(sub);

        var formCard = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(pal.Surface),
            BorderBrush = new SolidColorBrush(pal.Separator),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 12, 14, 12)
        };

        var form = new Grid();
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        form.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        form.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var lblName = new TextBlock { Text = "Name:", Foreground = textBrush, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
        Grid.SetRow(lblName, 0); Grid.SetColumn(lblName, 0);
        form.Children.Add(lblName);

        var nameRow = new DockPanel();
        var nameEdit = new TextBox
        {
            Text = suggestedName,
            Width = 175,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(8, 4, 8, 4)
        };
        var pinCheck = new CheckBox
        {
            Content = new TextBlock { Text = "Pinned", Foreground = textBrush },
            IsChecked = true,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        var defCheck = new CheckBox
        {
            Content = new TextBlock { Text = "Set as Default", Foreground = textBrush },
            IsChecked = false,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        nameRow.Children.Add(nameEdit);
        nameRow.Children.Add(pinCheck);
        nameRow.Children.Add(defCheck);
        Grid.SetRow(nameRow, 0); Grid.SetColumn(nameRow, 1);
        form.Children.Add(nameRow);

        var lblPat = new TextBlock { Text = "Detected Pattern:", Foreground = textBrush, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
        Grid.SetRow(lblPat, 2); Grid.SetColumn(lblPat, 0);
        form.Children.Add(lblPat);

        var candCombo = new ComboBox { Padding = new Thickness(8, 4, 8, 4) };
        foreach (var c in candidates)
            candCombo.Items.Add(c.Label);
        if (candCombo.Items.Count > 0)
            candCombo.SelectedIndex = 0;
        Grid.SetRow(candCombo, 2); Grid.SetColumn(candCombo, 1);
        form.Children.Add(candCombo);

        var lblTpl = new TextBlock { Text = "URL / Template:", Foreground = textBrush, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
        Grid.SetRow(lblTpl, 4); Grid.SetColumn(lblTpl, 0);
        form.Children.Add(lblTpl);

        var tplEdit = new TextBox
        {
            Text = defaultTpl,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(8, 4, 8, 4)
        };
        Grid.SetRow(tplEdit, 4); Grid.SetColumn(tplEdit, 1);
        form.Children.Add(tplEdit);

        candCombo.SelectionChanged += (_, _) =>
        {
            int idx = candCombo.SelectedIndex;
            if (idx >= 0 && idx < candidates.Count)
                tplEdit.Text = candidates[idx].Template;
        };

        formCard.Child = form;
        Grid.SetRow(formCard, 2);
        root.Children.Add(formCard);

        var btnBar = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        var insBtn = new Button
        {
            Content = "Insert {selection}",
            Padding = new Thickness(12, 6, 12, 6),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        insBtn.Click += (_, _) =>
        {
            if (!tplEdit.Text.Contains("{selection}", StringComparison.Ordinal))
                tplEdit.Text += "{selection}";
        };
        DockPanel.SetDock(insBtn, Dock.Left);
        btnBar.Children.Add(insBtn);

        var rightBtns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var saveBtn = new Button
        {
            Content = "Save Action",
            IsDefault = true,
            Padding = new Thickness(16, 6, 16, 6),
            Margin = new Thickness(0, 0, 8, 0),
            FontWeight = FontWeights.SemiBold
        };
        try { if (Application.Current?.TryFindResource("AccentButtonStyle") is Style s) saveBtn.Style = s; } catch { }

        saveBtn.Click += (_, _) =>
        {
            string n = Regex.Replace((nameEdit.Text ?? "").Trim(), @"[\t\r\n]+", " ");
            string t = Regex.Replace((tplEdit.Text ?? "").Trim(), @"[\t\r\n]+", "");
            if (string.IsNullOrEmpty(n) || !SmartParser.IsAllowedActionUri(t))
            {
                MessageBox.Show(this, "Please enter a valid name and a URL or app URI scheme.", "SparkBar", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            onSave?.Invoke(n, t, pinCheck.IsChecked == true, defCheck.IsChecked == true);
            Close();
        };

        var cancelBtn = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            Padding = new Thickness(14, 6, 14, 6)
        };
        cancelBtn.Click += (_, _) => Close();

        rightBtns.Children.Add(saveBtn);
        rightBtns.Children.Add(cancelBtn);
        DockPanel.SetDock(rightBtns, Dock.Right);
        btnBar.Children.Add(rightBtns);

        Grid.SetRow(btnBar, 3);
        root.Children.Add(btnBar);

        Content = root;

        SourceInitialized += (_, _) => W11WindowHelper.ApplyMicaWindowTheme(this, pal, useAcrylic: false);
        Closed += (_, _) => Native.TrimWorkingSet();
    }
}

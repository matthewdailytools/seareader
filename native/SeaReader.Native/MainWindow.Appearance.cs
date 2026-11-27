using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace SeaReader.Native;

public partial class MainWindow
{
    private const double AppearancePopupInset = 8;
    private const string ReaderLayoutPreference = "reading-page-layout";
    private static Task readerLayoutWrite = Task.CompletedTask;

    private void OnToggleAppearance(object sender, RoutedEventArgs e)
    {
        if (AppearancePopup.IsOpen)
        {
            AppearancePopup.IsOpen = false;
            return;
        }

        ClosePanel(false);
        ClosePageJump();
        DismissInlineSelection();
        TypographyDetails.IsExpanded = false;
        UpdateAppearancePopupBounds();
        AppearancePopup.CustomPopupPlacementCallback = PlaceReaderAppearance;
        AppearancePopup.IsOpen = true;
    }

    private CustomPopupPlacement[] PlaceReaderAppearance(Size popupSize, Size targetSize, Point offset)
    {
        var transform = PresentationSource.FromVisual(ReaderSurface)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var width = popupSize.Width / transform.M11;
        var height = popupSize.Height / transform.M22;
        var margin = AppearancePopupInset;
        var target = AppearanceButton.TranslatePoint(new Point(), ReaderSurface);
        var left = Math.Clamp(target.X + (AppearanceButton.ActualWidth - width) / 2, margin,
            Math.Max(margin, ReaderSurface.ActualWidth - width - margin));
        var top = Math.Clamp(target.Y + AppearanceButton.ActualHeight + 7, margin,
            Math.Max(margin, ReaderSurface.ActualHeight - height - margin));
        // WPF's custom placement works in screen pixels. The button and reading area use DIPs.
        return [new CustomPopupPlacement(transform.Transform(new Point(left, top)), PopupPrimaryAxis.None)];
    }

    private void UpdateAppearancePopupBounds()
    {
        AppearancePopupFrame.MaxHeight = Math.Max(0, ReaderSurface.ActualHeight - 2 * AppearancePopupInset);
        AppearancePopup.PlacementRectangle = new Rect(ReaderSurface.RenderSize);
    }

    private void OnAppearancePopupOpened(object? sender, EventArgs e)
    {
        AppearanceScroll.ScrollToTop();
        if (pdf == null)
        {
            SelectCurrentReaderLayout();
            UpdateReaderLayoutStatus();
            FontSizeSlider.Focus();
        }
        else ZoomPicker.Focus();
    }

    private async void OnAppearancePopupKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        if (e.Key == Key.F11 && modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            if (!e.IsRepeat) await ToggleReaderFullScreenAsync();
            return;
        }
        if (FontPicker.IsDropDownOpen || ZoomPicker.IsDropDownOpen || LineGuidePicker.IsDropDownOpen ||
            ParagraphAlignmentPicker.IsDropDownOpen) return;
        if (HandleReaderNavigationShortcut(e, modifiers)) return;
        if (HandleFontSizeShortcut(e, modifiers) || e.Key != Key.Escape) return;
        e.Handled = true;
        if (e.IsRepeat) return;
        AppearancePopup.IsOpen = false;
        AppearanceButton.Focus();
    }

    private static string NextReaderTheme(string theme) => theme switch
    {
        "light" => "sepia",
        "sepia" => "dark",
        "dark" => "system",
        _ => "light"
    };

    private static string ReaderThemeName(string theme) => theme switch
    {
        "light" => "浅色",
        "sepia" => "护眼",
        "dark" => "深色",
        _ => "系统"
    };

    private void UpdateThemeButton()
    {
        var current = systemTheme.HighContrast ? "对比度主题" : appearance.Theme == "system"
            ? (systemTheme.Dark ? "系统深色" : "系统浅色") : ReaderThemeName(appearance.Theme);
        var next = ReaderThemeName(NextReaderTheme(appearance.Theme));
        ThemeButton.Header = $"背景 · {current}";
        var description = systemTheme.HighContrast ? $"正在使用 Windows 对比度主题，点击将首选阅读背景设为{next}"
            : $"背景当前为{current}，点击切换到{next}";
        ReaderBackgroundHeading.Text = systemTheme.HighContrast ? "阅读背景 · 系统对比度" : "阅读背景";
        ThemeButton.ToolTip = description;
        AutomationProperties.SetName(ThemeButton, description);
        SelectCurrentTheme();
    }

    private void SelectCurrentTheme()
    {
        var choices = ThemeChoices.Children.OfType<RadioButton>();
        var current = choices.FirstOrDefault(choice => Equals(choice.Tag, appearance.Theme)) ?? choices.First();
        current.IsChecked = true;
    }

    private void OnReaderThemeChecked(object sender, RoutedEventArgs e)
    {
        if (!populating && sender is RadioButton { Tag: string theme }) SetReaderTheme(theme);
    }

    private void OnCycleTheme(object sender, RoutedEventArgs e) => SetReaderTheme(NextReaderTheme(appearance.Theme));

    private void SetReaderTheme(string theme)
    {
        if (appearance.Theme == theme) return;
        appearance = appearance with { Theme = theme };
        ApplyReaderTheme();
        QueueAppearanceSave();
    }

    private void OnResetTypography(object sender, RoutedEventArgs e)
    {
        var defaults = new ReaderAppearance();
        var reset = appearance with
        {
            Font = defaults.Font, Size = defaults.Size, LineHeight = defaults.LineHeight, Margin = defaults.Margin,
            Bold = defaults.Bold, Alignment = defaults.Alignment, Hyphenation = defaults.Hyphenation
        };
        if (appearance == reset) return;
        ChangeAppearance(reset);
        UpdateAppearanceControls();
    }

    private void OnReaderLayoutChecked(object sender, RoutedEventArgs e)
    {
        if (populating || closing || sender is not RadioButton { Tag: string value }) return;
        var layout = NormalizeReaderLayout(value);
        if (appearance.Layout == layout) return;
        ChangeAppearance(appearance with { Layout = layout });
        UpdateReaderLayoutStatus();
        readerLayoutWrite = SaveReaderLayoutAfterAsync(readerLayoutWrite, layout);
    }

    private void SelectCurrentReaderLayout()
    {
        var layout = NormalizeReaderLayout(appearance.Layout);
        var prior = populating;
        populating = true;
        try
        {
            foreach (var choice in ReaderLayoutChoices.Children.OfType<RadioButton>())
                choice.IsChecked = Equals(choice.Tag, layout);
        }
        finally { populating = prior; }
    }

    private static string NormalizeReaderLayout(string? value) => value is "single" or "double" ? value : "auto";

    private async Task<string> LoadReaderLayoutPreferenceAsync(string? legacyLayout)
    {
        await readerLayoutWrite;
        var saved = await store.GetPreferenceAsync(ReaderLayoutPreference);
        return NormalizeReaderLayout(saved ?? legacyLayout);
    }

    private async Task SaveReaderLayoutAfterAsync(Task prior, string layout)
    {
        // Reader windows share an application preference and the same dispatcher.
        await prior;
        try { await store.SavePreferenceAsync(ReaderLayoutPreference, layout); }
        catch (Exception error) { if (!closing) ShowStatus(error.Message); }
    }

    private static string ReadPageTurnStyle(ReaderAppearance value) =>
        value.PageTurnStyle is "paper" or "fade" or "none"
            ? value.PageTurnStyle : value.AnimatePageTurns ? "paper" : "none";

    private PageTurnEffect ConfiguredPageTurnEffect => ReadPageTurnStyle(appearance) switch
    {
        "fade" => PageTurnEffect.Fade,
        "none" => PageTurnEffect.None,
        _ => PageTurnEffect.Paper
    };

    private void UpdatePageTurnEffectMenu()
    {
        var style = ReadPageTurnStyle(appearance);
        foreach (var item in TurnMotionMenu.Items.OfType<MenuItem>())
        {
            item.IsChecked = Equals(item.Tag, style);
            if (item.IsChecked) AutomationProperties.SetItemStatus(TurnMotionMenu, $"当前为{item.Header}");
        }
    }

    private void OnPageTurnEffectChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string style } || style is not ("paper" or "fade" or "none")) return;
        if (ReadPageTurnStyle(appearance) != style)
        {
            ResetPageTransition();
            appearance = appearance with { PageTurnStyle = style, AnimatePageTurns = style != "none" };
            QueueAppearanceSave();
        }
        UpdatePageTurnEffectMenu();
    }

    private void OnParagraphAlignmentChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ParagraphAlignmentPicker?.SelectedItem is ComboBoxItem { Tag: string value }) ChangeAppearance(appearance with { Alignment = value });
    }

    private void OnReaderBoldChanged(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { IsChecked: bool enabled }) ChangeAppearance(appearance with { Bold = enabled });
    }

    private void OnReaderHyphenationChanged(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { IsChecked: bool enabled }) ChangeAppearance(appearance with { Hyphenation = enabled });
    }

    private void QueueAppearanceSave()
    {
        appearanceTimer.Stop();
        appearanceTimer.Start();
    }
}

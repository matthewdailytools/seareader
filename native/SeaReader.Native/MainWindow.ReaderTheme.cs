using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace SeaReader.Native;

public partial class MainWindow
{
    private WindowsThemeState systemTheme;
    private IDisposable? themeObserver;
    private int themeRefreshQueued;

    private void OnSystemThemeInitialized(object? sender, EventArgs e)
    {
        themeObserver = WindowsReaderTheme.Watch(QueueSystemThemeRefresh);
        Closed += OnSystemThemeClosed;
        ApplyReaderPalette(WindowsReaderTheme.Read());
    }

    private void OnSystemThemeClosed(object? sender, EventArgs e)
    {
        themeObserver?.Dispose();
        themeObserver = null;
    }

    private void QueueSystemThemeRefresh()
    {
        if (Dispatcher.HasShutdownStarted || Interlocked.Exchange(ref themeRefreshQueued, 1) != 0) return;
        _ = Dispatcher.InvokeAsync(RefreshSystemTheme, DispatcherPriority.Background);
    }

    private void RefreshSystemTheme()
    {
        Interlocked.Exchange(ref themeRefreshQueued, 0);
        if (closing || themeObserver == null) return;
        var state = WindowsReaderTheme.Read();
        if (state != systemTheme || state.HighContrast) ApplyReaderPalette(state);
    }

    private void ApplyReaderTheme() => ApplyReaderPalette(systemTheme);

    private void ApplyReaderPalette(WindowsThemeState state)
    {
        var wasHighContrast = systemTheme.HighContrast;
        systemTheme = state;
        ResetPageTransition();
        if (state.HighContrast) ApplyContrastReaderColors();
        else ApplyReaderColors(appearance.Theme == "dark" || appearance.Theme == "system" && state.Dark,
            appearance.Theme == "sepia");
        if (state.HighContrast != wasHighContrast) ApplyReaderSelectionColors(state.HighContrast);

        UpdateThemeButton();
        UpdateLibraryColors();
        WindowsReaderTheme.SetDarkCaption(this, state.Dark && !state.HighContrast);
        QueueLineGuideRefresh();
        RefreshSavedTextMarks();
        RefreshTextSearchMatch();
        PdfSelection.InvalidateVisual();
        PdfFacingSelection.InvalidateVisual();
    }

    private void ApplyReaderColors(bool dark, bool sepia)
    {
        BrushColor("WindowSurface", dark ? "#182227" : sepia ? "#EBE2D1" : "#EDF1F1");
        BrushColor("PaperSurface", dark ? "#233237" : sepia ? "#F9F1E0" : "#F7FAF9");
        BrushColor("ToolbarSurface", dark ? "#233237" : sepia ? "#FAF2E2" : "#F7FAF9");
        BrushColor("FloatingReaderSurface", dark ? "#EB233237" : sepia ? "#EBFAF2E2" : "#EBF7FAF9");
        BrushColor("MainInk", dark ? "#E7F0EF" : sepia ? "#3E3222" : "#202F34");
        BrushColor("MutedInk", dark ? "#AAC0C2" : sepia ? "#6B5840" : "#53676C");
        BrushColor("BorderInk", dark ? "#41565B" : sepia ? "#D7C8AD" : "#CAD7D7");
        BrushColor("AccentInk", dark ? "#8DD3C6" : "#276864");
        BrushColor("HoverSurface", dark ? "#30484C" : sepia ? "#EEE4CE" : "#E0ECEA");
        BrushColor("ReaderPaper", dark ? "#202C31" : sepia ? "#F7EEDE" : "#FFFFFF");
        BrushColor("ReaderLinkInk", dark ? "#9AD4CA" : sepia ? "#315E57" : "#276864");
        BrushColor("ReaderPaperBorder", dark ? "#39494E" : sepia ? "#DCCDB0" : "#D5DEDF");
        BrushColor("ReaderPrimaryFill", dark ? "#8DD3C6" : "#276864");
        BrushColor("ReaderPrimaryInk", dark ? "#182227" : "#FFFFFF");
    }

    private void ApplyContrastReaderColors()
    {
        var paper = SystemColors.WindowColor;
        var ink = SystemColors.WindowTextColor;
        BrushColor("WindowSurface", paper);
        BrushColor("PaperSurface", paper);
        BrushColor("ToolbarSurface", paper);
        BrushColor("FloatingReaderSurface", paper);
        BrushColor("MainInk", ink);
        BrushColor("MutedInk", ink);
        BrushColor("BorderInk", ink);
        BrushColor("AccentInk", ink);
        BrushColor("HoverSurface", paper);
        BrushColor("ReaderPaper", paper);
        BrushColor("ReaderLinkInk", SystemColors.HotTrackColor);
        BrushColor("ReaderPaperBorder", ink);
        BrushColor("ReaderPrimaryFill", SystemColors.HighlightColor);
        BrushColor("ReaderPrimaryInk", SystemColors.HighlightTextColor);
    }

    private void ApplyReaderSelectionColors(bool highContrast)
    {
        foreach (TextBoxBase body in new TextBoxBase[] { TextList, FacingTextList, FootnoteBody })
        {
            if (highContrast)
            {
                body.SelectionOpacity = 1;
                body.SetResourceReference(TextBoxBase.SelectionBrushProperty, SystemColors.HighlightBrushKey);
                body.SetResourceReference(TextBoxBase.SelectionTextBrushProperty, SystemColors.HighlightTextBrushKey);
            }
            else
            {
                body.ClearValue(TextBoxBase.SelectionOpacityProperty);
                body.ClearValue(TextBoxBase.SelectionBrushProperty);
                body.ClearValue(TextBoxBase.SelectionTextBrushProperty);
            }
        }
    }

    private void BrushColor(string key, string value) => BrushColor(key, (Color)ColorConverter.ConvertFromString(value));

    private void BrushColor(string key, Color color)
    {
        if (Resources[key] is not SolidColorBrush existing || existing.Color != color)
            Resources[key] = new SolidColorBrush(color);
    }
}

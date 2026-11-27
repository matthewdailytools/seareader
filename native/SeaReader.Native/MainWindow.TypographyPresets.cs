using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SeaReader.Native;

public partial class MainWindow
{
    private sealed record ReaderTypography(string Font, double LineHeight, double Margin, bool Bold, string Alignment);

    private static readonly Dictionary<string, ReaderTypography> TypographyStyles = new(StringComparer.Ordinal)
    {
        ["classic"] = new("book", 1.8, 32, false, "left"),
        ["airy"] = new("serif", 2.0, 44, false, "left"),
        ["bold"] = new("system-ui", 1.8, 32, true, "left")
    };

    private static ReaderTypography ReadTypography(ReaderAppearance value) =>
        new(value.Font, value.LineHeight, value.Margin, value.Bold, value.Alignment);

    private static ReaderAppearance WithTypography(ReaderAppearance value, ReaderTypography typography) => value with
    {
        Font = typography.Font,
        LineHeight = typography.LineHeight,
        Margin = typography.Margin,
        Bold = typography.Bold,
        Alignment = typography.Alignment
    };

    private static string? ReadTypographyPreset(ReaderTypography typography)
    {
        // Slider tick arithmetic can differ from the preset by a few floating-point bits.
        var candidate = typography with { LineHeight = Math.Round(typography.LineHeight, 8) };
        return TypographyStyles.FirstOrDefault(preset => preset.Value == candidate).Key;
    }

    private ReaderAppearance TrackCustomTypography(ReaderAppearance value)
    {
        var typography = ReadTypography(value);
        return typography != ReadTypography(appearance) && ReadTypographyPreset(typography) == null
            ? value with { CustomTypography = typography }
            : value;
    }

    private void UpdateTypographyPresets()
    {
        var typography = ReadTypography(appearance);
        var preset = ReadTypographyPreset(typography);
        var prior = populating;
        populating = true;
        try
        {
            foreach (var choice in TypographyPresets.Children.OfType<RadioButton>())
                choice.IsChecked = Equals(choice.Tag, preset);
            var custom = appearance.CustomTypography;
            RestoreCustomTypographyButton.Visibility = custom == null ? Visibility.Collapsed : Visibility.Visible;
            RestoreCustomTypographyButton.IsEnabled = custom != null && custom != typography;
            FontSizeDecreaseButton.IsEnabled = appearance.Size > FontSizeSlider.Minimum;
            FontSizeIncreaseButton.IsEnabled = appearance.Size < FontSizeSlider.Maximum;
        }
        finally { populating = prior; }
    }

    private void OnTypographyPresetChecked(object sender, RoutedEventArgs e)
    {
        if (populating || sender is not RadioButton { Tag: string name } ||
            !TypographyStyles.TryGetValue(name, out var typography)) return;
        var current = ReadTypography(appearance);
        var custom = ReadTypographyPreset(current) == null ? current : appearance.CustomTypography;
        ChangeAppearance(WithTypography(appearance, typography) with { CustomTypography = custom }, preserveCustomTypography: true);
        UpdateAppearanceControls();
    }

    private void OnRestoreCustomTypography(object sender, RoutedEventArgs e)
    {
        if (appearance.CustomTypography is not { } custom) return;
        ChangeAppearance(WithTypography(appearance, custom), preserveCustomTypography: true);
        TypographyDetails.IsExpanded = true;
        UpdateAppearanceControls();
    }

    private void OnStepFontSize(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string step } || step is not ("-1" or "1")) return;
        var delta = step == "1" ? 1 : -1;
        SetReaderFontSize(FontSizeSlider.Value + delta);
    }

    private bool HandleFontSizeShortcut(KeyEventArgs e, ModifierKeys modifiers)
    {
        if (text == null || !ReaderSurface.IsVisible || closing || moving || turningPage || seekingPosition ||
            Keyboard.FocusedElement is TextBox { IsReadOnly: false } ||
            (modifiers != ModifierKeys.Control && !(modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.OemPlus)))
            return false;

        var size = e.Key switch
        {
            Key.OemPlus or Key.Add => FontSizeSlider.Value + 1,
            Key.OemMinus or Key.Subtract => FontSizeSlider.Value - 1,
            Key.D0 or Key.NumPad0 => DefaultReaderFontSize,
            _ => double.NaN
        };
        if (double.IsNaN(size)) return false;

        e.Handled = true;
        SetReaderFontSize(size);
        return true;
    }

    private void SetReaderFontSize(double size) => FontSizeSlider.Value = Math.Clamp(size, FontSizeSlider.Minimum, FontSizeSlider.Maximum);
}

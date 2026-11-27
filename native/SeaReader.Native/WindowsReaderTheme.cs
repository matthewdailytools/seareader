using Microsoft.Win32;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Interop;
using Windows.UI.ViewManagement;

namespace SeaReader.Native;

internal readonly record struct WindowsThemeState(bool Dark, bool HighContrast);

internal static class WindowsReaderTheme
{
    private const int ImmersiveDarkMode = 20;
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    internal static WindowsThemeState Read()
    {
        var background = SystemColors.WindowColor;
        bool dark = ReadWindowsAppTheme() ?? ReadSavedAppTheme() ?? !IsLight(background.R, background.G, background.B);
        return new WindowsThemeState(dark, SystemParameters.HighContrast);
    }

    internal static IDisposable Watch(Action changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        return new ThemeWatch(changed);
    }

    internal static void SetDarkCaption(Window window, bool dark)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        int enabled = dark ? 1 : 0;
        try { _ = DwmSetWindowAttribute(handle, ImmersiveDarkMode, ref enabled, sizeof(int)); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { }
    }

    private static bool? ReadWindowsAppTheme()
    {
        try
        {
            var foreground = new UISettings().GetColorValue(UIColorType.Foreground);
            return foreground.A == 0 ? null : IsLight(foreground.R, foreground.G, foreground.B);
        }
        catch (Exception error) when (IsThemeApiUnavailable(error)) { return null; }
    }

    private static bool? ReadSavedAppTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") switch { 0 => true, 1 => false, _ => null };
        }
        catch (Exception error) when (error is SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    private static bool IsLight(byte red, byte green, byte blue) => 2 * red + 5 * green + blue > 8 * 128;

    private static bool IsThemeApiUnavailable(Exception error) => error is ExternalException or InvalidCastException or
        InvalidOperationException or NotSupportedException or TypeLoadException or TypeInitializationException or
        UnauthorizedAccessException;

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, uint size);

    private sealed class ThemeWatch : IDisposable
    {
        private Action? changed;
        private UISettings? settings;
        private readonly bool watchingPreferences;

        internal ThemeWatch(Action changed)
        {
            this.changed = changed;
            SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
            try
            {
                var source = new UISettings();
                source.ColorValuesChanged += OnColorValuesChanged;
                settings = source;
            }
            catch (Exception error) when (IsThemeApiUnavailable(error)) { }

            // The desktop preference event also handles Windows versions that do not deliver the WinRT color event.
            try
            {
                SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
                watchingPreferences = true;
            }
            catch (Exception error) when (error is InvalidOperationException or ExternalException) { }
        }

        private void OnColorValuesChanged(UISettings sender, object args) => Notify();

        private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(SystemParameters.HighContrast)) Notify();
        }

        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or
                UserPreferenceCategory.VisualStyle or UserPreferenceCategory.Accessibility or UserPreferenceCategory.Window)
                Notify();
        }

        private void Notify() => Volatile.Read(ref changed)?.Invoke();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref changed, null) == null) return;
            SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
            if (watchingPreferences) SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            if (settings is { } source)
            {
                settings = null;
                try { source.ColorValuesChanged -= OnColorValuesChanged; }
                catch (Exception error) when (IsThemeApiUnavailable(error)) { }
            }
        }
    }
}

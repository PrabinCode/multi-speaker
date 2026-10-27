using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace MultiSpeaker;

/// <summary>
/// Switches between light and dark: WPF's Fluent theme styles the controls, and the brushes
/// below color our own parts (window, card, custom title bar).
/// </summary>
static class ThemeManager
{
    const int DwmwaUseImmersiveDarkMode = 20;
    const int DwmwaWindowCornerPreference = 33;
    const int DwmwcpRound = 2;

    public static bool SystemPrefersDark() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0;

    public static void Apply(bool dark)
    {
        var app = Application.Current;
        app.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;

        var r = app.Resources;
        r["WindowBackgroundBrush"] = Brush(dark ? "#202020" : "#F3F3F3");
        r["CardBrush"] = Brush(dark ? "#2B2B2B" : "#FFFFFF");
        r["CardBorderBrush"] = Brush(dark ? "#3A3A3A" : "#E3E3E3");
        r["FieldBrush"] = Brush(dark ? "#323232" : "#FBFBFB");
        r["TextBrush"] = Brush(dark ? "#FFFFFF" : "#1B1B1B");
        r["MutedTextBrush"] = Brush(dark ? "#A6A6A6" : "#616161");
        r["CaptionHoverBrush"] = Brush(dark ? "#333333" : "#E3E3E3");
        r["CaptionPressedBrush"] = Brush(dark ? "#3D3D3D" : "#D6D6D6");
    }

    /// <summary>Dark window border and Windows 11 rounded corners; needs the window handle.</summary>
    public static void ApplyToWindow(Window window, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;
        int value = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
        int corner = DwmwcpRound;
        DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));
    }

    static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

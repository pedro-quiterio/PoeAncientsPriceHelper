using System.Windows;

// System.Drawing (WinForms) and System.Windows.Media both define Brush/Color, so the Media types are
// aliased here to keep the preset table readable. Only the WPF window background is themed.
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using LinearGradientBrush = System.Windows.Media.LinearGradientBrush;

namespace PoeAncientsPriceHelper;

// Dark-theme presets, shared by every window. Apply() writes the chosen background onto
// Application.Current.Resources so the DynamicResource binding on each window's Background updates
// live across the whole app — the main window and the (separate) settings window stay in sync when
// the theme is changed. A key set directly on Application.Resources overrides the same key supplied
// by the merged WPF-UI ThemesDictionary. Only the window background changes; the FlatButton colors
// are hardcoded and never themed.
internal static class ThemePresets
{
    public const string Default = "Toxic";
    public const string BackgroundKey = "ApplicationBackgroundBrush";

    public static readonly (string Name, Brush Background)[] All =
    [
        ("Midnight", Solid(0x1C, 0x1C, 0x1C)),
        ("Obsidian", Gradient(0x05, 0x05, 0x05, 0x14, 0x14, 0x14)),
        ("Abyss",    Gradient(0x0A, 0x0F, 0x1E, 0x0D, 0x15, 0x28)),
        ("Ember",    Gradient(0x1A, 0x0F, 0x08, 0x0F, 0x08, 0x05)),
        ("Toxic",    Gradient(0x0A, 0x12, 0x08, 0x0D, 0x1A, 0x0A)),
    ];

    public static string[] Names => All.Select(t => t.Name).ToArray();

    // Falls back to the default preset when the saved name is unknown (e.g. a renamed/removed theme).
    public static string Resolve(string? saved) => All.Any(t => t.Name == saved) ? saved! : Default;

    public static void Apply(string name)
    {
        var preset = Array.Find(All, t => t.Name == name);
        if (preset.Name is null) return;
        System.Windows.Application.Current.Resources[BackgroundKey] = preset.Background;
    }

    private static Brush Solid(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Brush Gradient(byte r1, byte g1, byte b1, byte r2, byte g2, byte b2)
    {
        var brush = new LinearGradientBrush(Color.FromRgb(r1, g1, b1), Color.FromRgb(r2, g2, b2), 90);
        brush.Freeze();
        return brush;
    }
}

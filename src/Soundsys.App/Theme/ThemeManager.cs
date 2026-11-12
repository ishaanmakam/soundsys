using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Soundsys.App.Theme;

/// <summary>
/// Light/dark: Fluent (ThemeMode) for the stock controls plus our own palette dictionary (Theme/Light.xaml or Dark.xaml),
/// merged last so its accent overrides win. Follows the system setting live.
/// </summary>
internal static class ThemeManager
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private static ResourceDictionary? _palette;
    private static bool _following;

    public static bool IsDark { get; private set; }

    /// <summary>Raised after the palette changes (tray icon redraw).</summary>
    public static event EventHandler? Changed;

    /// <summary>True if Windows apps use the dark theme.</summary>
    public static bool SystemAppsDark() => ReadDword("AppsUseLightTheme") == 0;

    /// <summary>True if the taskbar (and so the tray) is dark.</summary>
    public static bool SystemTaskbarDark() => ReadDword("SystemUsesLightTheme") != 1;

    /// <summary>Applies a fixed theme (screenshots, tests).</summary>
    public static void Apply(Application app, bool dark) => Apply(app, dark, ThemeMode.System, fixedMode: true);

    /// <summary>Follows the Windows setting now and whenever it changes.</summary>
    public static void FollowSystem(Application app)
    {
        Apply(app, SystemAppsDark(), ThemeMode.System, fixedMode: false);
        if (_following)
        {
            return;
        }

        _following = true;
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
            {
                // Background priority: run after WPF has swapped its own Fluent dictionary, so ours stays last.
                app.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => Apply(app, SystemAppsDark(), ThemeMode.System, fixedMode: false));
            }
        };
    }

    private static void Apply(Application app, bool dark, ThemeMode systemMode, bool fixedMode)
    {
        var mode = fixedMode ? (dark ? ThemeMode.Dark : ThemeMode.Light) : systemMode;
        if (app.ThemeMode != mode)
        {
            app.ThemeMode = mode;
        }

        var merged = app.Resources.MergedDictionaries;
        if (_palette is not null)
        {
            merged.Remove(_palette);
        }

        _palette = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/soundsys;component/Theme/{(dark ? "Dark" : "Light")}.xaml", UriKind.Absolute),
        };
        merged.Add(_palette);
        var changed = IsDark != dark;
        IsDark = dark;
        if (changed || !fixedMode)
        {
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    private static int? ReadDword(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue(name) is int v ? v : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return null;
        }
    }
}

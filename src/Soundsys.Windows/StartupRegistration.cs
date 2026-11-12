using Microsoft.Win32;
using Soundsys.Engine.Abstractions;

namespace Soundsys.Windows;

/// <summary>"Start with Windows": HKCU\Software\Microsoft\Windows\CurrentVersion\Run, value "soundsys" = "\"exe\" --tray".</summary>
public sealed class StartupRegistration : IStartupRegistration
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly string _valueName;
    private readonly string _exePath;

    /// <param name="exePath">Executable to start; defaults to the current process.</param>
    /// <param name="valueName">Registry value name (tests use a unique one).</param>
    public StartupRegistration(string? exePath = null, string valueName = "soundsys")
    {
        _exePath = exePath ?? Environment.ProcessPath ?? throw new InvalidOperationException("Can't determine the executable path.");
        _valueName = valueName;
    }

    /// <summary>The command line written to the Run key.</summary>
    public string Command => BuildCommand(_exePath);

    public static string BuildCommand(string exePath) => $"\"{exePath}\" --tray";

    /// <summary>True if the Run value exists and points at this executable (a stale path from an old location counts as off).</summary>
    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(_valueName) is string value && IsOurs(value, _exePath);
        }
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (enabled)
            key.SetValue(_valueName, Command, RegistryValueKind.String);
        else if (key.GetValue(_valueName) is not null)
            key.DeleteValue(_valueName, throwOnMissingValue: false);
    }

    /// <summary>True if a Run command line launches <paramref name="exePath"/> (quotes and case ignored).</summary>
    public static bool IsOurs(string commandLine, string exePath)
    {
        var s = commandLine.Trim();
        string target;
        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            target = end > 0 ? s[1..end] : s.Trim('"');
        }
        else
        {
            var space = s.IndexOf(' ', StringComparison.Ordinal);
            target = space > 0 ? s[..space] : s;
        }

        return string.Equals(Path.GetFullPath(target), Path.GetFullPath(exePath), StringComparison.OrdinalIgnoreCase);
    }
}

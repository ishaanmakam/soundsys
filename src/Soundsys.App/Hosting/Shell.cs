using System.Diagnostics;

namespace Soundsys.App;

/// <summary>Small shell helpers.</summary>
internal static class Shell
{
    /// <summary>Opens a web page or an ms-settings: page. Failures are logged, never thrown.</summary>
    public static void OpenUri(string uri)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Trace.TraceError($"soundsys: couldn't open {uri}: {ex.Message}");
        }
    }
}

using System.Diagnostics;
using System.IO;

namespace Soundsys.App.Hosting;

/// <summary>Trace log at %LOCALAPPDATA%\soundsys\soundsys.log (kept under ~1 MB; the previous one is soundsys.old.log).</summary>
internal static class Log
{
    private const long MaxBytes = 1 << 20;

    public static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "soundsys", "soundsys.log");

    public static void Start()
    {
        try
        {
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var info = new FileInfo(path);
            if (info.Exists && info.Length > MaxBytes)
            {
                File.Move(path, Path.ChangeExtension(path, ".old.log"), overwrite: true);
            }

            var writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
            Trace.Listeners.Add(new TextWriterTraceListener(writer) { TraceOutputOptions = TraceOptions.DateTime });
            Trace.AutoFlush = true;
            Trace.TraceInformation($"soundsys {AppHost.Version} starting");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No log file is not a reason to fail.
        }
    }
}

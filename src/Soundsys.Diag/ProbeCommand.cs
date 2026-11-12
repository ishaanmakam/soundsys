using Soundsys.Windows;

namespace Soundsys.Diag;

/// <summary><c>soundsys-diag probe [--out &lt;file&gt;] [--no-phone-start]</c>: the silent Windows probe.</summary>
internal static class ProbeCommand
{
    public const string Name = "probe";

    public static async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        string? outPath = null;
        var tryPhoneStart = true;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--out" when i + 1 < args.Count:
                    outPath = args[++i];
                    break;
                case "--no-phone-start":
                    tryPhoneStart = false;
                    break;
                default:
                    Console.Error.WriteLine($"Unknown option '{args[i]}'. Usage: soundsys-diag probe [--out <file>] [--no-phone-start]");
                    return 2;
            }
        }

        var report = await WindowsProbe.RunAsync(tryPhoneStart);
        Console.WriteLine(report);
        if (outPath is not null)
        {
            var full = Path.GetFullPath(outPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, report.ReplaceLineEndings("\n"));
            Console.WriteLine($"Saved to {full}");
        }

        return 0;
    }
}

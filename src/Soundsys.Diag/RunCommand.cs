using System.Diagnostics;
using Soundsys.Engine;
using Soundsys.Engine.Abstractions;
using Soundsys.Engine.Settings;
using Soundsys.Engine.Simulation;
using Soundsys.Windows;

namespace Soundsys.Diag;

/// <summary>
/// <c>soundsys-diag [run] [--simulated] [--quick] [--drift-hour] [--minutes N] [--only a,b] [--skip a,b] [--out file]</c>:
/// every hardware check in order, one-line instructions, waits by detection, writes PHASE0_RESULTS.md.
/// </summary>
internal static class RunCommand
{
    public const string Usage = """
        soundsys-diag — hardware checks for soundsys

          soundsys-diag                 run every check (asks you to do a few things; waits until it sees them)
          soundsys-diag --drift-hour    also log speaker sync for an hour
          soundsys-diag --minutes 5     shorter stutter test (default 10 minutes per mode)
          soundsys-diag --only tones,calibration
          soundsys-diag --skip stutter
          soundsys-diag --out results.md
          soundsys-diag --simulated     run against simulated devices (silent; for testing the tool)
          soundsys-diag --quick         short durations
          soundsys-diag probe           silent environment probe only

        Checks: environment, speakers, tones, phone, suppression, stutter, calibration, reconnect, drift
        """;

    public static async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        if (!TryParse(args, out var options, out var error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine(Usage);
            return 2;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Console.WriteLine("Stopping… (restoring everything)");
        };

        SimScheduler? sim = null;
        IAudioBackend backend;
        ITimerScheduler scheduler;
        ISettingsStore store;
        IPowerManager? power = null;
        IOperator op;
        if (options.Simulated)
        {
            sim = new SimScheduler();
            sim.StartRealTime();
            backend = new SimulatedBackend(sim);
            scheduler = sim;
            store = new InMemorySettingsStore();
            op = new SimulatedOperator();
        }
        else
        {
            PlaybackSuppressor.RestoreAfterCrash();
            backend = new WindowsAudioBackend();
            scheduler = new ThreadTimerScheduler(QpcClock.Instance);
            store = new JsonSettingsStore(JsonSettingsStore.DefaultPath); // results (delays, suppression) carry over to the app
            power = new PowerManager();
            op = new ConsoleOperator();
        }

        var engine = new SyncEngine(backend, scheduler, store, power);
        var ctx = new DiagContext(options, backend, engine, op, backend as SimulatedBackend);
        var report = new ReportWriter(options.OutputPath, options);
        var results = Checks.All.Select(c => new CheckResult(c.Id, c.Title)).ToList();
        var environment = options.Simulated
            ? "Simulated devices: " + string.Join(", ", backend.GetSpeakers().Select(s => s.Name))
            : await SafeProbeAsync().ConfigureAwait(false);

        Console.WriteLine($"soundsys-diag {Program.Version} — {(options.Simulated ? "SIMULATED devices" : "real hardware")}");
        Console.WriteLine($"Results file: {Path.GetFullPath(options.OutputPath)}");
        Console.WriteLine("Press Ctrl+C to stop at any time; everything is restored.");
        Console.WriteLine();
        var exit = 0;
        try
        {
            for (var i = 0; i < Checks.All.Length; i++)
            {
                var (id, title) = Checks.All[i];
                var result = results[i];
                if ((options.Only.Count > 0 && !options.Only.Contains(id)) || options.Skip.Contains(id))
                {
                    result.Summary = "Not selected for this run.";
                    continue;
                }

                Console.WriteLine($"[{i + 1}/{Checks.All.Length}] {title}");
                var sw = Stopwatch.StartNew();
                try
                {
                    await Checks.Run(id, ctx, result, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    result.Set(Verdict.Skipped, "Stopped by you (Ctrl+C).");
                    break;
                }
                catch (Exception ex)
                {
                    result.Set(Verdict.Fail, $"Unexpected problem: {ex.Message}");
                    result.Add($"`{ex.GetType().Name}` at {ex.TargetSite?.DeclaringType?.Name}.{ex.TargetSite?.Name}");
                }
                finally
                {
                    result.Duration = sw.Elapsed;
                    Console.WriteLine($"   {Label(result.Verdict)} {result.Summary}");
                    Console.WriteLine();
                    report.Write(results, environment);
                }

                if (result.Verdict == Verdict.Fail)
                {
                    exit = 1;
                }
            }
        }
        finally
        {
            engine.Dispose();
            backend.Dispose();
            sim?.Dispose();
            (power as IDisposable)?.Dispose();
            report.Write(results, environment);
        }

        Console.WriteLine("Summary:");
        foreach (var r in results)
        {
            Console.WriteLine($"  {Label(r.Verdict),-8} {r.Title}");
        }

        Console.WriteLine();
        Console.WriteLine($"Wrote {Path.GetFullPath(options.OutputPath)}");
        return exit;
    }

    private static string Label(Verdict v) => v switch
    {
        Verdict.Pass => "PASS",
        Verdict.Warn => "WARN",
        Verdict.Fail => "FAIL",
        _ => "SKIPPED",
    };

    private static async Task<string> SafeProbeAsync()
    {
        try
        {
            return await WindowsProbe.RunAsync(tryPhoneStart: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return $"Probe failed: {ex.Message}";
        }
    }

    internal static bool TryParse(IReadOnlyList<string> args, out DiagOptions options, out string error)
    {
        bool simulated = false, quick = false, drift = false;
        var minutes = 10.0;
        string? output = null;
        var only = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var known = Checks.All.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        error = string.Empty;
        options = null!;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "run":
                    break;
                case "--simulated":
                    simulated = true;
                    break;
                case "--quick":
                    quick = true;
                    break;
                case "--drift-hour":
                    drift = true;
                    break;
                case "--minutes" when i + 1 < args.Count && double.TryParse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture, out var m) && m > 0:
                    minutes = m;
                    i++;
                    break;
                case "--out" when i + 1 < args.Count:
                    output = args[++i];
                    break;
                case "--only" when i + 1 < args.Count:
                case "--skip" when i + 1 < args.Count:
                    var set = args[i] == "--only" ? only : skip;
                    foreach (var id in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (!known.Contains(id))
                        {
                            error = $"Unknown check '{id}'.";
                            return false;
                        }

                        set.Add(id);
                    }

                    break;
                default:
                    error = $"Unknown option '{args[i]}'.";
                    return false;
            }
        }

        options = new DiagOptions(simulated, quick, drift, minutes, output ?? DefaultOutput(), only, skip);
        return true;
    }

    /// <summary>docs/PHASE0_RESULTS.md in the repo if run from inside it, else next to where it's run.</summary>
    private static string DefaultOutput()
    {
        for (var dir = new DirectoryInfo(Environment.CurrentDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "soundsys.slnx")))
            {
                return Path.Combine(dir.FullName, "docs", "PHASE0_RESULTS.md");
            }
        }

        return Path.Combine(Environment.CurrentDirectory, "PHASE0_RESULTS.md");
    }
}

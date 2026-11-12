using System.Globalization;
using System.Text;
using Soundsys.Engine;
using Soundsys.Engine.Abstractions;
using Soundsys.Engine.Simulation;

namespace Soundsys.Diag;

public enum Verdict
{
    Pass,
    Warn,
    Fail,
    Skipped,
}

/// <summary>Outcome of one hardware check.</summary>
public sealed class CheckResult(string id, string title)
{
    public string Id { get; } = id;
    public string Title { get; } = title;
    public Verdict Verdict { get; set; } = Verdict.Skipped;
    public string Summary { get; set; } = "Not run.";
    public List<string> Details { get; } = [];
    public TimeSpan Duration { get; set; }

    public CheckResult Set(Verdict verdict, string summary)
    {
        Verdict = verdict;
        Summary = summary;
        return this;
    }

    public void Add(string line) => Details.Add(line);
}

public sealed record DiagOptions(
    bool Simulated,
    bool Quick,
    bool DriftHour,
    double StutterMinutes,
    string OutputPath,
    IReadOnlySet<string> Only,
    IReadOnlySet<string> Skip);

/// <summary>
/// The person running the diag. The console operator prints one-line instructions and waits for the hardware to change
/// (detection, not key presses); the simulated operator performs the physical action itself so the whole flow runs
/// unattended (CI, tonight).
/// </summary>
public interface IOperator
{
    /// <summary>Shows <paramref name="instruction"/> and waits until <paramref name="done"/> is true. Returns false on timeout.</summary>
    Task<bool> AskAsync(string instruction, Func<bool> done, TimeSpan timeout, Action? simulate, CancellationToken ct);

    void Say(string message);
}

public sealed class ConsoleOperator : IOperator
{
    public async Task<bool> AskAsync(string instruction, Func<bool> done, TimeSpan timeout, Action? simulate, CancellationToken ct)
    {
        if (done())
        {
            return true;
        }

        WriteInstruction(instruction);
        var end = DateTime.UtcNow + timeout;
        var lastReminder = DateTime.UtcNow;
        while (DateTime.UtcNow < end)
        {
            await Task.Delay(200, ct).ConfigureAwait(false);
            if (done())
            {
                return true;
            }

            if (DateTime.UtcNow - lastReminder > TimeSpan.FromSeconds(45))
            {
                lastReminder = DateTime.UtcNow;
                Console.WriteLine($"   …still waiting ({(end - DateTime.UtcNow).TotalSeconds:F0} s left). {instruction}");
            }
        }

        return false;
    }

    public void Say(string message) => Console.WriteLine("   " + message);

    private static void WriteInstruction(string instruction)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"▶ {instruction}");
        Console.ForegroundColor = previous;
    }
}

/// <summary>Performs the requested physical action (via the simulated backend) after a short, realistic pause.</summary>
public sealed class SimulatedOperator : IOperator
{
    public async Task<bool> AskAsync(string instruction, Func<bool> done, TimeSpan timeout, Action? simulate, CancellationToken ct)
    {
        if (done())
        {
            return true;
        }

        Console.WriteLine($"▶ {instruction}   [simulated user does this]");
        await Task.Delay(800, ct).ConfigureAwait(false);
        simulate?.Invoke();
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            if (done())
            {
                return true;
            }

            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        return false;
    }

    public void Say(string message) => Console.WriteLine("   " + message);
}

/// <summary>Everything a check needs.</summary>
public sealed class DiagContext(DiagOptions options, IAudioBackend backend, SyncEngine engine, IOperator op, SimulatedBackend? sim)
{
    public DiagOptions Options { get; } = options;
    public IAudioBackend Backend { get; } = backend;
    public SyncEngine Engine { get; } = engine;
    public IOperator Operator { get; } = op;

    /// <summary>Non-null in --simulated mode (lets checks script the "user").</summary>
    public SimulatedBackend? Sim { get; } = sim;

    /// <summary>Facts one check leaves for later ones (e.g. the phone capture path).</summary>
    public Dictionary<string, string> Items { get; } = [];

    public string NameA => Engine.GetStatus().Speakers[0].Name;
    public string NameB => Engine.GetStatus().Speakers[1].Name;

    public TimeSpan Wait(double realMinutes) => TimeSpan.FromMinutes(Options.Simulated ? Math.Min(realMinutes, 0.5) : realMinutes);

    /// <summary>Polls until <paramref name="condition"/> holds (no instruction shown).</summary>
    public static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken ct)
    {
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        return condition();
    }
}

/// <summary>Writes docs/PHASE0_RESULTS.md (rewritten after every check so partial runs still leave a report).</summary>
public sealed class ReportWriter(string path, DiagOptions options)
{
    private readonly DateTimeOffset _started = DateTimeOffset.Now;

    public string Path => path;

    public void Write(IReadOnlyList<CheckResult> results, string environment)
    {
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        sb.AppendLine("# Phase 0 hardware results");
        sb.AppendLine();
        sb.AppendLine(inv, $"Generated by `soundsys-diag` {Program.Version} on {_started:yyyy-MM-dd HH:mm} ({(options.Simulated ? "**simulated devices** — not real hardware" : "real hardware")}).");
        sb.AppendLine();
        sb.AppendLine("| Check | Result | Summary |");
        sb.AppendLine("|---|---|---|");
        foreach (var r in results)
        {
            sb.AppendLine(inv, $"| {r.Title} | {Badge(r.Verdict)} | {r.Summary.Replace("|", "\\|", StringComparison.Ordinal)} |");
        }

        sb.AppendLine();
        foreach (var r in results.Where(r => r.Details.Count > 0 || r.Verdict != Verdict.Skipped))
        {
            sb.AppendLine(inv, $"## {r.Title}");
            sb.AppendLine();
            var took = r.Duration > TimeSpan.Zero ? string.Create(inv, $" _(took {r.Duration.TotalSeconds:F0} s)_") : string.Empty;
            sb.AppendLine(inv, $"**{Badge(r.Verdict)}** — {r.Summary}{took}");
            sb.AppendLine();
            foreach (var d in r.Details)
            {
                sb.AppendLine(d.StartsWith('|') || d.StartsWith("```", StringComparison.Ordinal) ? d : "- " + d);
            }

            sb.AppendLine();
        }

        sb.AppendLine("## Environment");
        sb.AppendLine();
        sb.AppendLine("<details><summary>Probe output</summary>");
        sb.AppendLine();
        sb.AppendLine(environment);
        sb.AppendLine();
        sb.AppendLine("</details>");
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(path, sb.ToString());
    }

    private static string Badge(Verdict v) => v switch
    {
        Verdict.Pass => "✅ PASS",
        Verdict.Warn => "⚠️ WARN",
        Verdict.Fail => "❌ FAIL",
        _ => "⏭️ SKIPPED",
    };
}

/// <summary>Tone detection (Goertzel) for the distinct-tones check.</summary>
public static class ToneMeter
{
    /// <summary>Level of <paramref name="freq"/> relative to the median of nearby off-tone probes, in dB.</summary>
    public static (double LevelDb, double SnrDb) Measure(ReadOnlySpan<float> x, int rate, double freq)
    {
        var level = Goertzel(x, rate, freq);
        var noise = new List<double>();
        for (var k = -8; k <= 8; k++)
        {
            if (Math.Abs(k) < 3)
            {
                continue;
            }

            noise.Add(Goertzel(x, rate, freq * (1 + (k * 0.025))));
        }

        noise.Sort();
        var floor = Math.Max(noise[noise.Count / 2], 1e-12);
        return (20 * Math.Log10(Math.Max(level, 1e-12)), 20 * Math.Log10(Math.Max(level, 1e-12) / floor));
    }

    private static double Goertzel(ReadOnlySpan<float> x, int rate, double freq)
    {
        var w = 2 * Math.PI * freq / rate;
        var coeff = 2 * Math.Cos(w);
        double s1 = 0, s2 = 0;
        for (var i = 0; i < x.Length; i++)
        {
            var s0 = x[i] + (coeff * s1) - s2;
            s2 = s1;
            s1 = s0;
        }

        var power = (s1 * s1) + (s2 * s2) - (coeff * s1 * s2);
        return Math.Sqrt(Math.Max(power, 0)) * 2 / Math.Max(1, x.Length);
    }
}

/// <summary>Plays a steady tone on one output device (used directly, without the engine).</summary>
public sealed class ToneTarget(double frequency, float amplitude) : IRenderTarget
{
    private int _rate = 48000;
    private int _channels = 2;
    private double _phase;
    private long _frames;

    public void Prepare(AudioFormat deviceFormat, int maxFramesPerCallback)
    {
        _rate = deviceFormat.SampleRate;
        _channels = Math.Max(1, deviceFormat.Channels);
        _phase = 0;
        _frames = 0;
    }

    public void Render(Span<float> interleaved, int frames, in RenderTiming timing)
    {
        var inc = 2 * Math.PI * frequency / _rate;
        var fade = _rate * 0.05;
        for (var k = 0; k < frames; k++)
        {
            var env = Math.Min(1.0, (_frames + k) / fade);
            var v = (float)(amplitude * env * Math.Sin(_phase));
            _phase += inc;
            for (var c = 0; c < _channels; c++)
            {
                interleaved[(k * _channels) + c] = c < 2 ? v : 0f;
            }
        }

        _frames += frames;
        if (_phase > 2 * Math.PI * 1000)
        {
            _phase %= 2 * Math.PI;
        }
    }
}

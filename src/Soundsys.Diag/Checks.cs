using System.Diagnostics;
using System.Globalization;
using Soundsys.Engine;
using Soundsys.Engine.Abstractions;
using Soundsys.Engine.Settings;
using Soundsys.Engine.Simulation;
using Soundsys.Engine.Sync;
using Soundsys.Windows;

namespace Soundsys.Diag;

/// <summary>The hardware checks, in the order they run. Each one explains itself to the user and waits by detection.</summary>
internal static class Checks
{
    public static readonly (string Id, string Title)[] All =
    [
        ("environment", "Environment"),
        ("speakers", "Both speakers on"),
        ("tones", "Both speakers play at once (heard by the laptop mic)"),
        ("phone", "Phone → laptop (AudioPlaybackConnection)"),
        ("suppression", "No double audio from the laptop speakers"),
        ("stutter", "Stutter test: phone in + 2 out vs. 2 out only"),
        ("calibration", "Chirp calibration in the real room"),
        ("reconnect", "Speaker power-cycle rejoin"),
        ("drift", "1-hour drift log"),
    ];

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static Task Run(string id, DiagContext ctx, CheckResult r, CancellationToken ct) => id switch
    {
        "environment" => Environment(ctx, r),
        "speakers" => Speakers(ctx, r, ct),
        "tones" => Tones(ctx, r, ct),
        "phone" => Phone(ctx, r, ct),
        "suppression" => Suppression(ctx, r, ct),
        "stutter" => Stutter(ctx, r, ct),
        "calibration" => Calibration(ctx, r, ct),
        "reconnect" => Reconnect(ctx, r, ct),
        "drift" => Drift(ctx, r, ct),
        _ => Task.CompletedTask,
    };

    // ───────────────────────── environment ─────────────────────────
    private static Task Environment(DiagContext ctx, CheckResult r)
    {
        var speakers = ctx.Backend.GetSpeakers();
        var phones = ctx.Backend.PhoneLink.GetPairedPhones();
        var mics = ctx.Backend.GetMicrophones();
        var def = ctx.Backend.GetDefaultSpeaker();
        r.Add($"OS: {System.Environment.OSVersion.VersionString}, process {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}, .NET {System.Environment.Version}");
        r.Add("Speakers: " + string.Join("; ", speakers.Select(s => $"{s.Name} ({(s.IsBluetooth ? "Bluetooth" : "wired/built-in")}, {s.State})")));
        r.Add("Phones paired for audio: " + (phones.Count == 0 ? "none" : string.Join(", ", phones.Select(p => p.Name))));
        r.Add("Microphones: " + (mics.Count == 0 ? "none" : string.Join(", ", mics.Select(m => $"{m.Name} ({m.State})"))));
        r.Add($"Windows default output: {def?.Name ?? "none"}");

        var bt = speakers.Count(s => s.IsBluetooth);
        var missing = new List<string>();
        if (bt < 2)
        {
            missing.Add($"only {bt} Bluetooth speaker(s) paired");
        }

        if (phones.Count == 0)
        {
            missing.Add("no phone paired");
        }

        if (!mics.Any(m => m.State == EndpointState.Active))
        {
            missing.Add("no working microphone");
        }

        r.Set(missing.Count == 0 ? Verdict.Pass : Verdict.Warn,
            missing.Count == 0 ? $"{bt} Bluetooth speakers, {phones.Count} phone(s), mic present" : "Missing: " + string.Join(", ", missing));
        return Task.CompletedTask;
    }

    // ───────────────────────── speakers on ─────────────────────────
    private static async Task Speakers(DiagContext ctx, CheckResult r, CancellationToken ct)
    {
        var engine = ctx.Engine;
        if (engine.Settings.SpeakerAId is null || engine.Settings.SpeakerBId is null)
        {
            var paired = await ctx.Operator.AskAsync(
                "Pair both JBL speakers with Windows: Settings → Bluetooth & devices → Add device (speakers on, in pairing mode).",
                () => engine.Settings.SpeakerAId is not null && engine.Settings.SpeakerBId is not null,
                ctx.Wait(5), null, ct).ConfigureAwait(false);
            if (!paired)
            {
                r.Set(Verdict.Fail, "Two Bluetooth speakers weren't found. Pair them in Windows Bluetooth settings and run soundsys-diag again.");
                return;
            }
        }

        var a = engine.Settings.SpeakerAId!;
        var b = engine.Settings.SpeakerBId!;
        bool BothOn()
        {
            var s = ctx.Backend.GetSpeakers();
            return s.Any(x => x.Id == a && x.State == EndpointState.Active) && s.Any(x => x.Id == b && x.State == EndpointState.Active);
        }

        var on = await ctx.Operator.AskAsync(
            $"Turn on both speakers: {ctx.NameA} ({engine.DisplayName(a)}) and {ctx.NameB}. Windows connects them automatically.",
            BothOn, ctx.Wait(3), () =>
            {
                ctx.Sim?.SetSpeakerAvailable(a, true);
                ctx.Sim?.SetSpeakerAvailable(b, true);
            }, ct).ConfigureAwait(false);
        var status = engine.GetStatus();
        foreach (var s in status.Speakers)
        {
            r.Add($"{s.Name} = {s.DeviceName}" + (s.BatteryPercent is { } bat ? $", battery {bat}%" : ", battery not reported by Windows"));
        }

        r.Set(on ? Verdict.Pass : Verdict.Fail, on
            ? $"{status.Speakers[0].Name} ({status.Speakers[0].DeviceName}) and {status.Speakers[1].Name} ({status.Speakers[1].DeviceName}) are connected"
            : "Both speakers didn't connect within 3 minutes. Check they're on, charged and paired.");
    }

    // ───────────────────────── distinct tones ─────────────────────────
    private static async Task Tones(DiagContext ctx, CheckResult r, CancellationToken ct)
    {
        const double fa = 880, fb = 1320;
        ctx.Engine.Stop();
        var ids = (ctx.Engine.Settings.SpeakerAId, ctx.Engine.Settings.SpeakerBId);
        if (ids.SpeakerAId is null || ids.SpeakerBId is null)
        {
            r.Set(Verdict.Skipped, "No speakers selected.");
            return;
        }

        IOutputDevice? da = null, db = null;
        IAudioSource? mic = null;
        var rec = new MicRecorder(8);
        try
        {
            mic = ctx.Backend.CreateMicrophone(null);
            mic.Start(rec);
            await Task.Delay(400, ct).ConfigureAwait(false);
            ctx.Operator.Say($"Playing {fa:F0} Hz on {ctx.NameA} and {fb:F0} Hz on {ctx.NameB} together for 3 seconds…");
            da = ctx.Backend.CreateOutput(ids.SpeakerAId);
            db = ctx.Backend.CreateOutput(ids.SpeakerBId);
            da.Start(new ToneTarget(fa, 0.25f));
            db.Start(new ToneTarget(fb, 0.25f));
            await Task.Delay(3500, ct).ConfigureAwait(false);
        }
        catch (AudioDeviceException ex)
        {
            r.Set(Verdict.Fail, ex.Message);
            return;
        }
        finally
        {
            da?.Dispose();
            db?.Dispose();
            await Task.Delay(300, CancellationToken.None).ConfigureAwait(false);
            mic?.Dispose();
        }

        var samples = rec.Samples;
        var rate = Math.Max(1, rec.SampleRate);
        var take = Math.Min(samples.Length, (int)(2.5 * rate));
        if (take < rate)
        {
            r.Set(Verdict.Fail, "The microphone didn't record anything. Check microphone access in Windows privacy settings.");
            return;
        }

        var window = samples.Slice(samples.Length - take - (int)(0.4 * rate) > 0 ? samples.Length - take - (int)(0.4 * rate) : 0, take);
        var (la, sa) = ToneMeter.Measure(window, rate, fa);
        var (lb, sb) = ToneMeter.Measure(window, rate, fb);
        r.Add($"{ctx.NameA} ({fa:F0} Hz): {la:F1} dBFS at the mic, {sa:F1} dB above the noise");
        r.Add($"{ctx.NameB} ({fb:F0} Hz): {lb:F1} dBFS at the mic, {sb:F1} dB above the noise");
        const double heard = 12;
        if (sa >= heard && sb >= heard)
        {
            r.Set(Verdict.Pass, $"Both speakers played at the same time ({sa:F0} dB / {sb:F0} dB above noise)");
        }
        else if (sa >= heard || sb >= heard)
        {
            r.Set(Verdict.Fail, $"Only {(sa >= heard ? ctx.NameA : ctx.NameB)} was heard. The other speaker may not support two streams at once, or is too quiet / too far away.");
        }
        else
        {
            r.Set(Verdict.Fail, "Neither tone was heard. Turn the speakers up and keep the laptop within a few metres.");
        }
    }

    // ───────────────────────── phone link ─────────────────────────
    private static async Task Phone(DiagContext ctx, CheckResult r, CancellationToken ct)
    {
        var link = ctx.Backend.PhoneLink;
        if (link.GetPairedPhones().Count == 0)
        {
            var paired = await ctx.Operator.AskAsync(
                "Pair your phone with this laptop: on the phone open Bluetooth settings and pick this laptop (accept the code on both).",
                () => link.GetPairedPhones().Count > 0, ctx.Wait(4), null, ct).ConfigureAwait(false);
            if (!paired)
            {
                r.Set(Verdict.Skipped, "No phone paired. Pair one in Windows Bluetooth settings and re-run with --only phone.");
                return;
            }
        }

        var micsBefore = ctx.Backend.GetMicrophones().Select(m => m.Id).ToHashSet();
        ctx.Engine.SetMode(SourceMode.Phone);
        if (!ctx.Engine.Start())
        {
            r.Set(Verdict.Fail, ctx.Engine.GetStatus().Error ?? "soundsys couldn't start.");
            return;
        }

        var sw = Stopwatch.StartNew();
        var first = link.GetPairedPhones()[0];
        var flowing = await ctx.Operator.AskAsync(
            "On your phone, choose this laptop as the speaker (iPhone: Control Center → AirPlay icon → this laptop; Android: Bluetooth), then press play.",
            () => ctx.Engine.GetStatus() is { AudioFlowing: true, SourceName: not null },
            ctx.Wait(4),
            () => ctx.Sim?.Phones.SimulatePlay(first.Id), ct).ConfigureAwait(false);

        foreach (var p in link.GetStatus())
        {
            r.Add($"{p.Name}: {p.State}" + (p.Detail is null ? string.Empty : $" — {p.Detail}"));
        }

        if (!flowing)
        {
            var errors = link.GetStatus().Where(p => p.Detail is not null).Select(p => p.Detail).ToList();
            r.Set(Verdict.Fail, errors.Count > 0
                ? $"No audio arrived from the phone. Windows said: {errors[0]}"
                : "No audio arrived from the phone within 4 minutes (the laptop wasn't chosen as the speaker, or Windows didn't expose the stream).");
            return;
        }

        var status = ctx.Engine.GetStatus();
        var newMics = ctx.Backend.GetMicrophones().Where(m => !micsBefore.Contains(m.Id)).ToList();
        var path = newMics.Count > 0
            ? $"a capture endpoint Windows created for the phone (\"{newMics[0].Name}\")"
            : "loopback of the Windows default output (no phone capture endpoint appeared)";
        ctx.Items["phonePath"] = newMics.Count > 0 ? "endpoint" : "loopback";
        r.Add($"Capture path: {path}");
        r.Add($"Time from instruction to audio: {sw.Elapsed.TotalSeconds:F1} s");
        r.Add($"Engine headline: \"{status.Headline}\"");
        r.Set(Verdict.Pass, $"Audio is arriving from {status.SourceName} via {(newMics.Count > 0 ? "a phone capture endpoint" : "default-output loopback")}");
    }

    // ───────────────────────── double-audio suppression ─────────────────────────
    private static async Task Suppression(DiagContext ctx, CheckResult r, CancellationToken ct)
    {
        if (ctx.Options.Simulated)
        {
            r.Set(Verdict.Skipped, "Needs real Windows audio (loopback + mute); not meaningful with simulated devices.");
            return;
        }

        if (ctx.Engine.GetStatus() is not { AudioFlowing: true, Mode: SourceMode.Phone })
        {
            r.Set(Verdict.Skipped, "Needs phone audio playing (the phone check didn't pass).");
            return;
        }

        var laptopOut = await WindowsHelpers.MeasureDefaultOutputDbAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        var captured = Db(ctx.Engine.GetDiagnostics().SourceLevel);
        r.Add($"Phone audio captured by soundsys: {captured:F1} dBFS");
        r.Add($"Laptop default output while the phone plays: {laptopOut:F1} dBFS");
        if (laptopOut < -60)
        {
            ctx.Engine.UpdateSettings(s => s.PhoneSuppression = PlaybackSuppression.None);
            r.Set(Verdict.Pass, "Windows isn't also playing the phone on the laptop speakers. Nothing to suppress.");
            return;
        }

        r.Add("The laptop's own output carries the phone audio too, so it would double up. Trying fixes (each one is undone afterwards):");
        var phone = ctx.Engine.GetStatus().SourceName ?? string.Empty;
        var candidates = new (PlaybackSuppression Mode, string Label, Func<PlaybackSuppressor> Make)[]
        {
            (PlaybackSuppression.MuteSourceSession, "mute only the phone's audio session", () => PlaybackSuppressor.ForSessions(new SessionMatcher([], [phone]))),
            (PlaybackSuppression.MuteEndpointDuringLoopback, "mute the laptop output while soundsys runs", () => PlaybackSuppressor.ForEndpointMute()),
        };
        foreach (var (mode, label, make) in candidates)
        {
            using var suppressor = make();
            try
            {
                suppressor.Apply();
                await Task.Delay(1500, ct).ConfigureAwait(false);
                var stillCaptured = Db(ctx.Engine.GetDiagnostics().SourceLevel);
                var outNow = await WindowsHelpers.MeasureDefaultOutputDbAsync(TimeSpan.FromSeconds(1.5), ct).ConfigureAwait(false);
                r.Add($"• {label}: soundsys still captures {stillCaptured:F1} dBFS; laptop output now {outNow:F1} dBFS");
                if (stillCaptured > -60)
                {
                    ctx.Engine.UpdateSettings(s => s.PhoneSuppression = mode);
                    r.Set(Verdict.Pass, $"Fixed: soundsys will {label} (saved). Capture keeps working.");
                    return;
                }
            }
            finally
            {
                suppressor.Restore();
            }
        }

        r.Set(Verdict.Fail, "Muting the laptop output also silences the capture on this audio driver, so the laptop speakers will play along. Workaround: Laptop mode, or a virtual audio cable (not installed).");
    }

    // ───────────────────────── stutter ─────────────────────────
    private static async Task Stutter(DiagContext ctx, CheckResult r, CancellationToken ct)
    {
        var minutes = ctx.Options.Quick ? 0.3 : ctx.Options.StutterMinutes;
        var engine = ctx.Engine;
        if (engine.GetStatus().State == EngineState.Stopped)
        {
            engine.Start();
        }

        r.Add("| Mode | Minutes | Underruns | Inbound gaps filled | Seconds without audio | Latency target |");
        r.Add("|---|---|---|---|---|---|");

        var phoneRow = (Underruns: -1L, Minutes: 0.0);
        engine.SetMode(SourceMode.Phone);
        var phones = ctx.Backend.PhoneLink.GetPairedPhones();
        var playing = phones.Count > 0 && await ctx.Operator.AskAsync(
            $"Keep music playing on the phone for the next {minutes:F0} minutes. Don't touch anything.",
            () => engine.GetStatus().AudioFlowing, ctx.Wait(2),
            () => ctx.Sim?.Phones.SimulatePlay(phones[0].Id), ct).ConfigureAwait(false);
        if (playing)
        {
            var row = await Measure(ctx, "Phone in + 2 out", minutes, ct).ConfigureAwait(false);
            r.Add(row.Line);
            phoneRow = (row.Underruns, minutes);
        }
        else
        {
            r.Add("| Phone in + 2 out | — | not measured (no phone audio) | | | |");
        }

        ctx.Operator.Say("Now the laptop plays soft test music by itself (2 streams out only). You can pause the phone.");
        engine.StartTestSignal(new MusicLikeSignal(), "Test music");
        var laptop = await Measure(ctx, "2 out only (Laptop-mode load)", minutes, ct).ConfigureAwait(false);
        engine.StopTestSignal();
        r.Add(laptop.Line);

        var perMin = phoneRow.Underruns < 0 ? double.NaN : phoneRow.Underruns / Math.Max(phoneRow.Minutes, 0.01);
        var laptopPerMin = laptop.Underruns / Math.Max(minutes, 0.01);
        r.Add($"Engine latency target at the end: {engine.GetDiagnostics().BaseLatencyMs:F0} ms (auto-grows after repeated underruns).");
        if (double.IsNaN(perMin))
        {
            r.Set(laptopPerMin <= 0.2 ? Verdict.Warn : Verdict.Fail, $"Phone mode not measured; 2-out-only: {laptop.Underruns} underruns in {minutes:0.#} min");
        }
        else
        {
            var verdict = perMin <= 0.2 ? Verdict.Pass : perMin <= 1 ? Verdict.Warn : Verdict.Fail;
            r.Set(verdict, $"Phone in + 2 out: {phoneRow.Underruns} underruns in {minutes:0.#} min; 2 out only: {laptop.Underruns}");
        }
    }

    private static async Task<(string Line, long Underruns)> Measure(DiagContext ctx, string label, double minutes, CancellationToken ct)
    {
        var engine = ctx.Engine;
        var start = engine.GetDiagnostics();
        var silent = 0;
        var sw = Stopwatch.StartNew();
        var total = TimeSpan.FromMinutes(minutes);
        var nextReport = TimeSpan.FromMinutes(1);
        while (sw.Elapsed < total)
        {
            await Task.Delay(1000, ct).ConfigureAwait(false);
            if (!engine.GetStatus().AudioFlowing)
            {
                silent++;
            }

            if (sw.Elapsed >= nextReport)
            {
                nextReport += TimeSpan.FromMinutes(1);
                var now = engine.GetDiagnostics();
                ctx.Operator.Say($"{label}: {sw.Elapsed.TotalMinutes:F0}/{minutes:F0} min, {Underruns(now) - Underruns(start)} underruns so far");
            }
        }

        var end = engine.GetDiagnostics();
        var underruns = Underruns(end) - Underruns(start);
        var gapsMs = (end.GapFramesFilled - start.GapFramesFilled) / 48.0;
        return (string.Create(Inv, $"| {label} | {minutes:F1} | {underruns} | {gapsMs:F0} ms | {silent} | {end.BaseLatencyMs:F0} ms |"), underruns);
    }

    private static long Underruns(EngineDiagnostics d) => d.A.Underruns + d.B.Underruns;

    // ───────────────────────── calibration ─────────────────────────
    private static async Task Calibration(DiagContext ctx, CheckResult r, CancellationToken ct)
    {
        var engine = ctx.Engine;
        if (engine.GetStatus().State == EngineState.Stopped && !engine.Start())
        {
            r.Set(Verdict.Fail, engine.GetStatus().Error ?? "soundsys couldn't start.");
            return;
        }

        engine.StopTestSignal();
        await DiagContext.WaitUntilAsync(() => engine.GetStatus().Speakers.All(s => s.Link == SpeakerLinkState.Connected), TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        ctx.Operator.Say("Calibrating: put the laptop where you usually listen and keep the room quiet for about 20 seconds…");
        var first = await engine.CalibrateAsync(ct).ConfigureAwait(false);
        r.Add($"Pass 1 (applied): {first.Message} {ctx.NameA} {first.LatencyAMs:F1} ms, {ctx.NameB} {first.LatencyBMs:F1} ms, quality {first.QualityADb:F0}/{first.QualityBDb:F0} dB → delays {first.OffsetAMs:F1}/{first.OffsetBMs:F1} ms");
        if (!first.Success)
        {
            r.Set(Verdict.Fail, first.Message);
            return;
        }

        var residuals = new List<double>();
        for (var i = 2; i <= 3; i++)
        {
            await Task.Delay(1000, ct).ConfigureAwait(false);
            var m = await engine.MeasureSyncAsync(ct).ConfigureAwait(false);
            r.Add($"Pass {i} (check): {(m.Success ? $"remaining offset {m.RelativeMs:+0.0;-0.0} ms (quality {m.QualityADb:F0}/{m.QualityBDb:F0} dB)" : m.Message)}");
            if (m.Success)
            {
                residuals.Add(m.RelativeMs!.Value);
            }
        }

        var worst = residuals.Count == 0 ? double.NaN : residuals.Max(Math.Abs);
        r.Set(residuals.Count == 2 && worst <= 2 ? Verdict.Pass : Verdict.Warn,
            $"{first.Message} After correction the speakers are within {worst:F1} ms (target ≤ 2 ms).");
    }

    // ───────────────────────── reconnect ─────────────────────────
    private static async Task Reconnect(DiagContext ctx, CheckResult r, CancellationToken ct)
    {
        var engine = ctx.Engine;
        if (engine.GetStatus().State == EngineState.Stopped)
        {
            engine.Start();
        }

        var b = engine.Settings.SpeakerBId!;
        engine.StartTestSignal(new MusicLikeSignal(), "Test music");
        await DiagContext.WaitUntilAsync(() => engine.GetStatus().Speakers.All(s => s.Playing), TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        var deskBefore = engine.GetDiagnostics().A;
        var deskInterrupted = false;
        using var watch = new CancellationTokenSource();
        var watcher = Task.Run(async () =>
        {
            while (!watch.IsCancellationRequested)
            {
                if (!engine.GetStatus().Speakers[0].Playing)
                {
                    deskInterrupted = true;
                }

                await Task.Delay(100).ConfigureAwait(false);
            }
        }, CancellationToken.None);

        try
        {
            var off = await ctx.Operator.AskAsync($"Turn OFF the {ctx.NameB} speaker now (power button).",
                () => engine.GetStatus().Speakers[1].Link == SpeakerLinkState.Lost, ctx.Wait(3),
                () => ctx.Sim?.SetSpeakerAvailable(b, false), ct).ConfigureAwait(false);
            if (!off)
            {
                r.Set(Verdict.Skipped, $"{ctx.NameB} wasn't switched off within 3 minutes.");
                return;
            }

            var lostAt = Stopwatch.StartNew();
            r.Add($"{ctx.NameB} drop detected; {ctx.NameA} kept playing.");
            var back = await ctx.Operator.AskAsync($"Now turn {ctx.NameB} back ON.",
                () => ctx.Backend.GetSpeakers().Any(s => s.Id == b && s.State == EndpointState.Active), ctx.Wait(3),
                () => ctx.Sim?.SetSpeakerAvailable(b, true), ct).ConfigureAwait(false);
            if (!back)
            {
                r.Set(Verdict.Fail, $"Windows didn't see {ctx.NameB} come back within 3 minutes.");
                return;
            }

            var windowsBack = Stopwatch.StartNew();
            r.Add($"Windows reconnected {ctx.NameB} {lostAt.Elapsed.TotalSeconds:F1} s after the drop (includes your power-on time).");
            var rejoined = await DiagContext.WaitUntilAsync(() => engine.GetStatus().Speakers[1].Playing, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
            var rejoin = windowsBack.Elapsed.TotalSeconds;
            await watch.CancelAsync().ConfigureAwait(false);
            await watcher.ConfigureAwait(false);
            var deskAfter = engine.GetDiagnostics().A;
            r.Add($"soundsys playing on {ctx.NameB} again {rejoin:F1} s after Windows reconnected it.");
            r.Add($"{ctx.NameA} during the outage: {(deskInterrupted ? "interrupted" : "never interrupted")}, underruns +{deskAfter.Underruns - deskBefore.Underruns}.");

            if (rejoined && engine.Settings.Calibrations.Count > 0)
            {
                engine.StopTestSignal();
                var m = await engine.MeasureSyncAsync(ct).ConfigureAwait(false);
                r.Add(m.Success
                    ? $"Sync after rejoin with the saved delay: speakers within {Math.Abs(m.RelativeMs!.Value):F1} ms."
                    : $"Sync check after rejoin: {m.Message}");
            }

            r.Set(!rejoined ? Verdict.Fail : rejoin <= 10 && !deskInterrupted ? Verdict.Pass : Verdict.Warn,
                rejoined ? $"{ctx.NameB} rejoined {rejoin:F1} s after it came back (target ≤ 10 s); {ctx.NameA} {(deskInterrupted ? "was interrupted" : "kept playing")}"
                         : $"{ctx.NameB} came back in Windows but soundsys didn't resume it within 60 s.");
        }
        finally
        {
            await watch.CancelAsync().ConfigureAwait(false);
            engine.StopTestSignal();
        }
    }

    // ───────────────────────── drift hour ─────────────────────────
    private static async Task Drift(DiagContext ctx, CheckResult r, CancellationToken ct)
    {
        if (!ctx.Options.DriftHour)
        {
            r.Set(Verdict.Skipped, "Optional. Run `soundsys-diag --drift-hour` to log speaker sync for an hour (plays a short sweep every 5 minutes).");
            return;
        }

        var engine = ctx.Engine;
        if (engine.GetStatus().State == EngineState.Stopped)
        {
            engine.Start();
        }

        var total = TimeSpan.FromMinutes(ctx.Options.Quick ? 1 : 60);
        var interval = TimeSpan.FromMinutes(ctx.Options.Quick ? 0.25 : 5);
        ctx.Operator.Say($"Logging sync for {total.TotalMinutes:F0} minutes. Soft test music plays between measurements; leave everything as it is.");
        r.Add("| Minute | Speaker offset (ms) | Desk correction (ppm) | Bed correction (ppm) | Underruns |");
        r.Add("|---|---|---|---|---|");
        var sw = Stopwatch.StartNew();
        var values = new List<double>();
        while (sw.Elapsed <= total)
        {
            engine.StopTestSignal();
            var m = await engine.MeasureSyncAsync(ct).ConfigureAwait(false);
            engine.StartTestSignal(new MusicLikeSignal(), "Test music");
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            var d = engine.GetDiagnostics();
            r.Add(string.Create(Inv, $"| {sw.Elapsed.TotalMinutes:F1} | {(m.Success ? m.RelativeMs!.Value.ToString("+0.00;-0.00", Inv) : "not heard")} | {d.A.CorrectionPpm:F1} | {d.B.CorrectionPpm:F1} | {d.A.Underruns + d.B.Underruns} |"));
            if (m.Success)
            {
                values.Add(m.RelativeMs!.Value);
            }

            var next = interval - TimeSpan.FromSeconds(sw.Elapsed.TotalSeconds % interval.TotalSeconds);
            if (sw.Elapsed + next > total)
            {
                break;
            }

            await Task.Delay(next, ct).ConfigureAwait(false);
        }

        engine.StopTestSignal();
        if (values.Count < 2)
        {
            r.Set(Verdict.Fail, "Too few successful measurements.");
            return;
        }

        var spread = values.Max() - values.Min();
        r.Set(spread <= 2 ? Verdict.Pass : Verdict.Warn, $"Sync moved by {spread:F2} ms over {sw.Elapsed.TotalMinutes:F0} minutes ({values.Count} measurements; target ≤ 2 ms)");
    }

    private static double Db(double linear) => 20 * Math.Log10(Math.Max(linear, 1e-9));
}

/// <summary>Real-hardware helpers (Windows only).</summary>
internal static class WindowsHelpers
{
    /// <summary>RMS level of what Windows is playing on the default output, measured by loopback (silent).</summary>
    public static async Task<double> MeasureDefaultOutputDbAsync(TimeSpan duration, CancellationToken ct)
    {
        using var src = new WasapiCaptureSource(null, "Laptop output", CaptureMode.Loopback);
        var sink = new LevelSink();
        src.Start(sink);
        try
        {
            await Task.Delay(duration, ct).ConfigureAwait(false);
        }
        finally
        {
            src.Stop();
        }

        return sink.RmsDb;
    }

    private sealed class LevelSink : ISourceSink
    {
        private double _sum;
        private long _n;

        public double RmsDb => _n == 0 ? -120 : 10 * Math.Log10(Math.Max(_sum / _n, 1e-12));

        public void OnFormat(AudioFormat format)
        {
        }

        public void Write(ReadOnlySpan<float> interleaved, double timestampSeconds, bool discontinuity)
        {
            foreach (var v in interleaved)
            {
                _sum += v * v;
            }

            _n += interleaved.Length;
        }
    }
}

using System.Collections.Concurrent;
using Soundsys.Engine.Abstractions;
using Soundsys.Engine.Calibration;
using Soundsys.Engine.Dsp;
using Soundsys.Engine.Settings;
using Soundsys.Engine.Sync;

namespace Soundsys.Engine;

public enum EngineState
{
    Stopped,
    Running,
    Calibrating,
}

public enum SpeakerSlot
{
    A = 0,
    B = 1,
}

/// <summary>One speaker as the UI sees it.</summary>
public sealed record SpeakerStatus(
    SpeakerSlot Slot,
    string? EndpointId,
    string Name,
    string DeviceName,
    SpeakerLinkState Link,
    bool Available,
    bool Playing,
    int? BatteryPercent,
    double Volume,
    double OffsetMs,
    double LatencyErrorMs,
    double CorrectionPpm,
    long Underruns,
    double? LastOutageSeconds,
    string? Problem);

/// <summary>Everything the UI shows, captured atomically.</summary>
public sealed record EngineStatus(
    EngineState State,
    SourceMode Mode,
    string Headline,
    string? Detail,
    string? SourceName,
    bool AudioFlowing,
    IReadOnlyList<SpeakerStatus> Speakers,
    string? Error,
    string? Warning,
    double MasterVolume,
    string? CalibrationStep,
    bool ClickTrack,
    double BaseLatencyMs,
    double SyncMs);

/// <summary>Raw engine counters for diagnostics (soundsys-diag).</summary>
public sealed record EngineDiagnostics(
    ChannelStats A,
    ChannelStats B,
    long SourcePackets,
    long GapFramesFilled,
    double SourceLevel,
    double BaseLatencyMs,
    string? SourceName);

/// <summary>Result of auto-calibration, with plain-English message and the numbers.</summary>
public sealed record CalibrationReport(
    bool Success,
    string Message,
    double? LatencyAMs,
    double? LatencyBMs,
    double? RelativeMs,
    double OffsetAMs,
    double OffsetBMs,
    double QualityADb,
    double QualityBDb);

/// <summary>
/// The soundsys engine: one source in, two speakers out, kept in sync. Owns the shared history buffer, both output channels,
/// the reconnect state machines, phone handoff, calibration and settings. Hardware comes from an <see cref="IAudioBackend"/>
/// (real or simulated). Control methods are thread-safe; asynchronous device events are queued and handled on the 4 Hz
/// housekeeping tick, so every change shows up in <see cref="GetStatus"/> within a second.
/// </summary>
public sealed class SyncEngine : IDisposable
{
    public const double TickSeconds = 0.25;
    private const double AutoGrowWindowSeconds = 30;
    private const int AutoGrowUnderruns = 3;
    private const double AutoGrowStepMs = 10;
    private const double MaxBaseLatencyMs = 150;
    private const double FlowingLevel = 1e-4;
    private static readonly string[] LaptopApps = ["Spotify", "AppleMusic"];

    private readonly IAudioBackend _backend;
    private readonly ITimerScheduler _scheduler;
    private readonly ISettingsStore _store;
    private readonly IPowerManager? _power;
    private readonly Lock _lock = new();
    private readonly ConcurrentQueue<Action<double>> _pending = new();
    private readonly HistoryBuffer _history = new(1 << 19);
    private readonly SourceRouter _router;
    private readonly SharedSyncSettings _shared = new();
    private readonly OutputChannel[] _channels;
    private readonly SpeakerConnection[] _links = [new(), new()];
    private readonly IOutputDevice?[] _devices = new IOutputDevice?[2];
    private readonly string?[] _problems = new string?[2];
    private readonly string?[] _activeIds = new string?[2];
    private readonly PhoneHandoff _handoff = new();
    private readonly Queue<double> _underrunTimes = new();
    private readonly IDisposable _tick;

    private IAudioSource? _source;
    private string? _sourceName;
    private EngineState _state = EngineState.Stopped;
    private string? _error;
    private string? _calibrationStep;
    private bool _clickTrack;
    private bool _autoStartArmed = true;
    private bool _settingsDirty;
    private long _lastUnderrunTotal;

    public SyncEngine(IAudioBackend backend, ITimerScheduler scheduler, ISettingsStore store, IPowerManager? power = null)
    {
        _backend = backend;
        _scheduler = scheduler;
        _store = store;
        _power = power;
        Settings = store.Load();
        _router = new SourceRouter(_history);
        _channels = [new OutputChannel(_history, _shared), new OutputChannel(_history, _shared)];
        _shared.BaseLatencyMs = Settings.BaseLatencyMs;
        _shared.MasterGain = Taper(Settings.MasterVolume);

        _backend.DevicesChanged += (_, _) => Enqueue(OnDevicesChanged);
        _backend.PhoneLink.ActivityChanged += (_, e) => Enqueue(now => OnPhoneActivity(e.PhoneId, e.State));
        AutoSelectSpeakers();
        _tick = _scheduler.SchedulePeriodic(TickSeconds, Tick);
    }

    /// <summary>Live settings (treat as read-only outside the engine; use the engine's methods to change them).</summary>
    public AppSettings Settings { get; }

    public IAudioBackend Backend => _backend;

    public bool IsSimulated => _backend.IsSimulated;

    /// <summary>Endpoints changed (refresh speaker / phone lists).</summary>
    public event EventHandler? DevicesChanged;

    private double Now => _scheduler.Clock.NowSeconds;

    // ───────────────────────────── Control ─────────────────────────────

    /// <summary>Starts playback to both speakers. Returns false (and sets <see cref="EngineStatus.Error"/>) if it can't.</summary>
    public bool Start()
    {
        lock (_lock)
        {
            if (_state != EngineState.Stopped)
            {
                return true;
            }

            if (Settings.SpeakerAId is null || Settings.SpeakerBId is null)
            {
                _error = "Choose two speakers first (Speakers tab).";
                return false;
            }

            if (Settings.SpeakerAId == Settings.SpeakerBId)
            {
                _error = "Pick two different speakers.";
                return false;
            }

            _error = null;
            _state = EngineState.Running;
            _autoStartArmed = false;
            _underrunTimes.Clear();
            _shared.BaseLatencyMs = Settings.BaseLatencyMs;
            _shared.MasterGain = Taper(Settings.MasterVolume);
            _activeIds[0] = Settings.SpeakerAId;
            _activeIds[1] = Settings.SpeakerBId;
            ApplySpeakerSettings();
            foreach (var ch in _channels)
            {
                ch.ResetStats();
            }

            _lastUnderrunTotal = 0;
            _power?.SetKeepAwake(Settings.KeepAwake);
            var now = Now;
            for (var i = 0; i < 2; i++)
            {
                _problems[i] = null;
                if (_links[i].Start(now))
                {
                    TryOpen(i, now);
                }
            }

            StartModeSource();
            return true;
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (_state == EngineState.Stopped)
            {
                return;
            }

            StopInternal();
        }
    }

    public void SetMode(SourceMode mode)
    {
        lock (_lock)
        {
            if (Settings.Mode == mode)
            {
                return;
            }

            Settings.Mode = mode;
            MarkDirty();
            if (_state == EngineState.Running)
            {
                _clickTrack = false;
                StartModeSource();
            }
        }
    }

    /// <summary>Master volume, 0..1 (perceptual taper applied).</summary>
    public void SetMasterVolume(double volume)
    {
        lock (_lock)
        {
            Settings.MasterVolume = Math.Clamp(volume, 0, 1);
            _shared.MasterGain = Taper(Settings.MasterVolume);
            MarkDirty();
        }
    }

    public void SetSpeakerVolume(SpeakerSlot slot, double volume)
    {
        lock (_lock)
        {
            var id = SelectedId(slot);
            if (id is null)
            {
                return;
            }

            Settings.SpeakerVolumes[id] = Math.Clamp(volume, 0, 1);
            _channels[(int)slot].Gain = Taper(Settings.SpeakerVolumes[id]);
            MarkDirty();
        }
    }

    /// <summary>Manual sync: positive delays speaker B (Bed) by that many ms, negative delays speaker A (Desk).</summary>
    public void SetManualSync(double bMinusAMs)
    {
        lock (_lock)
        {
            var v = Math.Clamp(Math.Round(bMinusAMs), -500, 500);
            SetOffsets(Math.Max(0, -v), Math.Max(0, v), "manual", null);
        }
    }

    public double GetManualSync()
    {
        lock (_lock)
        {
            var (a, b) = CurrentOffsets();
            return b - a;
        }
    }

    public void SelectSpeaker(SpeakerSlot slot, string endpointId)
    {
        lock (_lock)
        {
            if (slot == SpeakerSlot.A)
            {
                if (Settings.SpeakerBId == endpointId)
                {
                    Settings.SpeakerBId = Settings.SpeakerAId;
                }

                Settings.SpeakerAId = endpointId;
            }
            else
            {
                if (Settings.SpeakerAId == endpointId)
                {
                    Settings.SpeakerAId = Settings.SpeakerBId;
                }

                Settings.SpeakerBId = endpointId;
            }

            MarkDirty();
            if (_state != EngineState.Stopped)
            {
                // Re-open both slots on the new selection (the pair's calibration changes too).
                var now = Now;
                for (var i = 0; i < 2; i++)
                {
                    var id = i == 0 ? Settings.SpeakerAId : Settings.SpeakerBId;
                    if (_activeIds[i] != id)
                    {
                        CloseDevice(i);
                        _activeIds[i] = id;
                        _links[i].Stop();
                        if (id is not null && _links[i].Start(now))
                        {
                            TryOpen(i, now);
                        }
                    }
                }
            }

            ApplySpeakerSettings();
        }
    }

    public void RenameSpeaker(string endpointId, string name)
    {
        lock (_lock)
        {
            name = name.Trim();
            if (name.Length == 0)
            {
                Settings.SpeakerNames.Remove(endpointId);
            }
            else
            {
                Settings.SpeakerNames[endpointId] = name.Length > 24 ? name[..24] : name;
            }

            MarkDirty();
        }
    }

    public void SetPhoneEnabled(string phoneId, bool enabled)
    {
        lock (_lock)
        {
            Settings.DisabledPhoneIds.Remove(phoneId);
            if (!enabled)
            {
                Settings.DisabledPhoneIds.Add(phoneId);
            }

            MarkDirty();
            if (_state != EngineState.Stopped && Settings.Mode == SourceMode.Phone)
            {
                _backend.PhoneLink.SetEnabledPhones(EnabledPhoneIds());
            }
        }
    }

    public void UpdateSettings(Action<AppSettings> change)
    {
        lock (_lock)
        {
            change(Settings);
            _shared.BaseLatencyMs = Settings.BaseLatencyMs;
            if (_state != EngineState.Stopped)
            {
                _power?.SetKeepAwake(Settings.KeepAwake);
            }

            MarkDirty();
            SaveIfDirty();
        }
    }

    public void StartClickTrack() => StartTestSignal(new ClickTrackSignal(), "Click track");

    /// <summary>Plays a generated signal on both speakers instead of the phone/laptop (click track, diagnostics).</summary>
    public void StartTestSignal(ISignal signal, string name)
    {
        lock (_lock)
        {
            if (_state != EngineState.Running)
            {
                return;
            }

            _clickTrack = true;
            StartSource(new GeneratorSource(_scheduler, signal, name), name);
        }
    }

    /// <summary>Stops a test signal started by <see cref="StartTestSignal"/> and returns to the normal source.</summary>
    public void StopTestSignal() => StopClickTrack();

    public void StopClickTrack()
    {
        lock (_lock)
        {
            if (!_clickTrack)
            {
                return;
            }

            _clickTrack = false;
            if (_state == EngineState.Running)
            {
                StartModeSource();
            }
        }
    }

    // ───────────────────────────── Queries ─────────────────────────────

    /// <summary>Speakers the user can choose: Bluetooth first (JBL first among them), then the rest.</summary>
    public IReadOnlyList<AudioEndpointInfo> GetSpeakerChoices() =>
        _backend.GetSpeakers()
            .OrderByDescending(s => s.IsBluetooth)
            .ThenByDescending(s => IsJbl(s.Name))
            .ThenBy(s => s.State)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public IReadOnlyList<(PhoneStatus Phone, bool Enabled)> GetPhones()
    {
        var statuses = _backend.PhoneLink.GetStatus();
        lock (_lock)
        {
            return statuses.Select(p => (p, !Settings.DisabledPhoneIds.Contains(p.Id))).ToList();
        }
    }

    public string DisplayName(string endpointId)
    {
        var info = _backend.GetSpeakers().FirstOrDefault(s => s.Id == endpointId);
        lock (_lock)
        {
            return Settings.NameFor(endpointId, info?.Name ?? "Speaker");
        }
    }

    public EngineStatus GetStatus()
    {
        var endpoints = _backend.GetSpeakers();
        var defaultSpeaker = _backend.GetDefaultSpeaker();
        lock (_lock)
        {
            var now = Now;
            var speakers = new SpeakerStatus[2];
            for (var i = 0; i < 2; i++)
            {
                var id = i == 0 ? Settings.SpeakerAId : Settings.SpeakerBId;
                var info = id is null ? null : endpoints.FirstOrDefault(e => e.Id == id);
                var stats = _channels[i].GetStats();
                var link = _state == EngineState.Stopped ? SpeakerLinkState.Idle : _links[i].State;
                var name = id is null ? (i == 0 ? "Speaker 1" : "Speaker 2") : Settings.NameFor(id, info?.Name ?? "Speaker");
                string? problem = null;
                if (_state != EngineState.Stopped && _links[i].IsProblem(now))
                {
                    problem = $"{name} is off or out of range";
                }

                speakers[i] = new SpeakerStatus(
                    (SpeakerSlot)i,
                    id,
                    name,
                    info?.Name ?? "Not chosen",
                    link,
                    info?.State == EndpointState.Active,
                    link == SpeakerLinkState.Connected && stats.State == ChannelState.Playing,
                    id is null ? null : _backend.GetBatteryPercent(id),
                    id is null ? 1.0 : Settings.VolumeFor(id),
                    _channels[i].OffsetMs,
                    stats.LatencyErrorMs,
                    stats.CorrectionPpm,
                    stats.Underruns,
                    _links[i].LastOutageSeconds,
                    problem);
            }

            var flowing = _state != EngineState.Stopped && _source is not null && _router.Level > FlowingLevel
                && now - _router.LastWriteTimestamp < 0.5;
            var (headline, detail) = Describe(speakers, flowing, now);
            string? warning = null;
            if (defaultSpeaker is not null && (defaultSpeaker.Id == Settings.SpeakerAId || defaultSpeaker.Id == Settings.SpeakerBId))
            {
                var n = Settings.NameFor(defaultSpeaker.Id, defaultSpeaker.Name);
                warning = $"Windows is also sending sound straight to {n}. In Windows sound settings, choose the laptop speakers as the output.";
            }

            var (a, b) = CurrentOffsets();
            return new EngineStatus(_state, Settings.Mode, headline, detail, _sourceName, flowing, speakers, _error, warning,
                Settings.MasterVolume, _calibrationStep, _clickTrack, _shared.BaseLatencyMs, b - a);
        }
    }

    public EngineDiagnostics GetDiagnostics()
    {
        lock (_lock)
        {
            return new EngineDiagnostics(_channels[0].GetStats(), _channels[1].GetStats(), _router.Packets, _router.GapFrames,
                _router.Level, _shared.BaseLatencyMs, _sourceName);
        }
    }

    internal OutputChannel ChannelFor(SpeakerSlot slot) => _channels[(int)slot];

    internal SourceRouter Router => _router;

    // ───────────────────────────── Calibration ─────────────────────────────

    /// <summary>Plays a sweep on each speaker in turn, listens with the laptop mic, and sets the per-speaker delays.</summary>
    public Task<CalibrationReport> CalibrateAsync(CancellationToken cancellationToken = default) => CalibrateCoreAsync(apply: true, cancellationToken);

    /// <summary>Same measurement as <see cref="CalibrateAsync"/> but leaves the delays unchanged (drift logging).</summary>
    public Task<CalibrationReport> MeasureSyncAsync(CancellationToken cancellationToken = default) => CalibrateCoreAsync(apply: false, cancellationToken);

    private async Task<CalibrationReport> CalibrateCoreAsync(bool apply, CancellationToken cancellationToken)
    {
        var plan = new CalibrationPlan();
        string nameA, nameB;
        lock (_lock)
        {
            if (_state != EngineState.Running)
            {
                return Fail("Press Start first, then calibrate.");
            }

            if (_devices[0] is null || _devices[1] is null)
            {
                return Fail("Turn on both speakers first.");
            }

            nameA = Settings.NameFor(Settings.SpeakerAId!, "Speaker 1");
            nameB = Settings.NameFor(Settings.SpeakerBId!, "Speaker 2");
            _state = EngineState.Calibrating;
            _clickTrack = false;
            _calibrationStep = "Getting ready… keep the room quiet";
            StopSource();
        }

        IAudioSource? mic = null;
        GeneratorSource? generator = null;
        var recorder = new MicRecorder(plan.TotalSeconds + plan.MaxLatencySeconds + 3);
        try
        {
            try
            {
                mic = _backend.CreateMicrophone(null);
                mic.Start(recorder);
            }
            catch (AudioDeviceException ex)
            {
                return Fail(ex.Message);
            }
            catch (Exception)
            {
                return Fail("Couldn't open the laptop microphone. Check that microphone access is on in Windows privacy settings.");
            }

            await Delay(0.3, cancellationToken).ConfigureAwait(false);

            double emitA, emitB;
            lock (_lock)
            {
                const int rate = 48000;
                var state = _history.ReadState();
                var w0 = state.SampleRate == rate ? state.Position : 0;
                var sweepFrames = (long)((plan.SweepSeconds + 0.05) * rate);
                var pA = w0 + (long)(plan.OffsetA * rate);
                var pB = w0 + (long)(plan.OffsetB * rate);
                _channels[0].SetPassWindow(pA - 1, pA + sweepFrames);
                _channels[1].SetPassWindow(pB - 1, pB + sweepFrames);
                generator = new GeneratorSource(_scheduler, new CalibrationSignal(plan), "Calibration", rate);
                StartSource(generator, "Calibration");
                emitA = generator.TimeOfFrame((long)(plan.OffsetA * rate));
                emitB = generator.TimeOfFrame((long)(plan.OffsetB * rate));
                _calibrationStep = $"Playing a test sweep on {nameA}…";
            }

            await Delay(plan.OffsetB, cancellationToken).ConfigureAwait(false);
            SetStep($"Playing a test sweep on {nameB}…");
            var listenUntil = emitB + plan.SweepSeconds + plan.MaxLatencySeconds + 0.2;
            await Delay(Math.Max(0.05, listenUntil - Now), cancellationToken).ConfigureAwait(false);
            SetStep("Measuring…");
            mic.Stop();

            var samples = recorder.Samples.ToArray();
            var analysis = await Task.Run(
                () => Calibrator.Analyze(samples, recorder.SampleRate, recorder.StartTime, emitA, emitB, plan),
                cancellationToken).ConfigureAwait(false);
            if (!analysis.A.Heard && !analysis.B.Heard)
            {
                return Fail($"Couldn't hear either speaker. Turn {nameA} and {nameB} up, keep the laptop within a few metres, and try again.");
            }

            if (!analysis.A.Heard || !analysis.B.Heard)
            {
                var missing = analysis.A.Heard ? nameB : nameA;
                return Fail($"Couldn't hear the {missing} speaker. Turn it up or move the laptop closer, then try again.");
            }

            lock (_lock)
            {
                var (curA, curB) = CurrentOffsets();
                var (offA, offB) = apply ? Calibrator.ComputeOffsets(curA, curB, analysis.RelativeDelayMs) : (curA, curB);
                if (apply)
                {
                    SetOffsets(offA, offB, "auto", (analysis.A.LatencySeconds * 1000, analysis.B.LatencySeconds * 1000));
                }

                var later = analysis.RelativeDelayMs >= 0 ? nameB : nameA;
                var earlier = analysis.RelativeDelayMs >= 0 ? nameA : nameB;
                var message = Math.Abs(analysis.RelativeDelayMs) < 1
                    ? "Your speakers were already in sync."
                    : apply
                        ? $"{later} was {Math.Abs(analysis.RelativeDelayMs):F0} ms behind, so {earlier} now waits for it."
                        : $"{later} is {Math.Abs(analysis.RelativeDelayMs):F1} ms behind {earlier}.";
                return new CalibrationReport(true, message, analysis.A.LatencySeconds * 1000, analysis.B.LatencySeconds * 1000,
                    analysis.RelativeDelayMs, offA, offB, analysis.A.QualityDb, analysis.B.QualityDb);
            }
        }
        catch (OperationCanceledException)
        {
            return Fail("Calibration cancelled.");
        }
        finally
        {
            mic?.Dispose();
            lock (_lock)
            {
                _channels[0].ClearPassWindow();
                _channels[1].ClearPassWindow();
                _calibrationStep = null;
                if (_state == EngineState.Calibrating)
                {
                    _state = EngineState.Running;
                    StartModeSource();
                }

                SaveIfDirty();
            }
        }

        CalibrationReport Fail(string message)
        {
            var (a, b) = CurrentOffsets();
            return new CalibrationReport(false, message, null, null, null, a, b, 0, 0);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_state != EngineState.Stopped)
            {
                StopInternal();
            }

            SaveIfDirty();
        }

        _tick.Dispose();
    }

    // ───────────────────────────── Internals ─────────────────────────────

    /// <summary>Volume slider (0..1) to linear gain: square law ≈ perceptually even steps.</summary>
    public static float Taper(double volume) => (float)(volume * volume);

    private static bool IsJbl(string name) => name.Contains("JBL", StringComparison.OrdinalIgnoreCase);

    private void Enqueue(Action<double> action) => _pending.Enqueue(action);

    private void Tick(double now)
    {
        lock (_lock)
        {
            while (_pending.TryDequeue(out var action))
            {
                try
                {
                    action(now);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _error = "Something went wrong talking to Windows audio. soundsys will keep trying.";
                    System.Diagnostics.Trace.TraceError($"soundsys engine: {ex}");
                }
            }

            if (_state != EngineState.Stopped)
            {
                for (var i = 0; i < 2; i++)
                {
                    if (_links[i].OnTick(now))
                    {
                        TryOpen(i, now);
                    }
                }

                AutoGrowLatency(now);
            }

            SaveIfDirty();
        }
    }

    private void OnDevicesChanged(double now)
    {
        if (_state != EngineState.Stopped)
        {
            for (var i = 0; i < 2; i++)
            {
                if (_links[i].OnDevicesChanged(now))
                {
                    TryOpen(i, now);
                }
            }
        }
        else
        {
            AutoSelectSpeakers();
            MaybeAutoStart();
        }

        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void MaybeAutoStart()
    {
        if (!Settings.AutoStartWhenSpeakersOn || Settings.SpeakerAId is null || Settings.SpeakerBId is null)
        {
            return;
        }

        var speakers = _backend.GetSpeakers();
        var bothOn = speakers.Any(s => s.Id == Settings.SpeakerAId && s.State == EndpointState.Active)
                     && speakers.Any(s => s.Id == Settings.SpeakerBId && s.State == EndpointState.Active);
        if (!bothOn)
        {
            _autoStartArmed = true; // re-arm once a speaker has been off
            return;
        }

        if (_autoStartArmed)
        {
            Start();
        }
    }

    private void OnPhoneActivity(string phoneId, PhoneLinkState state)
    {
        var decision = _handoff.OnActivity(phoneId, state);
        foreach (var other in decision.Release)
        {
            _backend.PhoneLink.Release(other);
        }

        if (decision.ActiveChanged && _state == EngineState.Running && Settings.Mode == SourceMode.Phone && !_clickTrack)
        {
            StartModeSource();
        }
    }

    private void StartModeSource()
    {
        var phones = _backend.PhoneLink;
        if (Settings.Mode == SourceMode.Phone)
        {
            phones.SetEnabledPhones(EnabledPhoneIds());
            var active = _handoff.ActivePhoneId;
            if (active is not null && !Settings.DisabledPhoneIds.Contains(active))
            {
                var name = phones.GetPairedPhones().FirstOrDefault(p => p.Id == active)?.Name ?? "your phone";
                TryStartSource(() => phones.CreateSource(active, Settings.PhoneSuppression), name);
            }
            else
            {
                StopSource();
            }
        }
        else
        {
            phones.SetEnabledPhones([]);
            TryStartSource(() => _backend.CreateLaptopSource(new LaptopCaptureOptions(true, LaptopApps, Settings.LaptopSuppression)), "this laptop");
        }
    }

    private void TryStartSource(Func<IAudioSource> create, string name)
    {
        try
        {
            StartSource(create(), name);
        }
        catch (AudioDeviceException ex)
        {
            StopSource();
            _error = ex.Message;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StopSource();
            _error = Settings.Mode == SourceMode.Phone
                ? "Couldn't receive audio from the phone. Try turning Bluetooth off and on on the phone."
                : "Couldn't capture this laptop's sound.";
            System.Diagnostics.Trace.TraceError($"soundsys source: {ex}");
        }
    }

    private void StartSource(IAudioSource source, string name)
    {
        StopSource();
        source.Faulted += (_, e) => Enqueue(now =>
        {
            if (ReferenceEquals(_source, source))
            {
                StopSource();
                if (_state == EngineState.Running)
                {
                    StartModeSource();
                }
            }
        });
        source.Start(_router);
        _source = source;
        _sourceName = name;
        if (_error is not null && !_error.StartsWith("Choose", StringComparison.Ordinal))
        {
            _error = null;
        }
    }

    private void StopSource()
    {
        var s = _source;
        _source = null;
        _sourceName = null;
        if (s is not null)
        {
            s.Stop();
            s.Dispose();
        }
    }

    private void TryOpen(int slot, double now)
    {
        var id = _activeIds[slot];
        if (id is null)
        {
            _links[slot].OnOpenFailed(now);
            return;
        }

        CloseDevice(slot);
        IOutputDevice? device = null;
        try
        {
            device = _backend.CreateOutput(id);
            var captured = device;
            device.Faulted += (_, e) =>
            {
                var at = Now;
                Enqueue(_ =>
                {
                    if (ReferenceEquals(_devices[slot], captured))
                    {
                        CloseDevice(slot);
                        _links[slot].OnFaulted(at);
                    }
                });
            };
            device.Start(_channels[slot]);
            _devices[slot] = device;
            _problems[slot] = null;
            _links[slot].OnOpenSucceeded(now);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            device?.Dispose();
            _problems[slot] = ex is AudioDeviceException ? ex.Message : null;
            _links[slot].OnOpenFailed(now);
        }
    }

    private void CloseDevice(int slot)
    {
        var d = _devices[slot];
        _devices[slot] = null;
        if (d is not null)
        {
            d.Stop();
            d.Dispose();
        }
    }

    private void StopInternal()
    {
        StopSource();
        for (var i = 0; i < 2; i++)
        {
            CloseDevice(i);
            _links[i].Stop();
        }

        _backend.PhoneLink.SetEnabledPhones([]);
        _handoff.Reset();
        _clickTrack = false;
        _state = EngineState.Stopped;
        _power?.SetKeepAwake(false);
    }

    private void AutoGrowLatency(double now)
    {
        var total = _channels[0].GetStats().Underruns + _channels[1].GetStats().Underruns;
        for (var k = _lastUnderrunTotal; k < total; k++)
        {
            _underrunTimes.Enqueue(now);
        }

        _lastUnderrunTotal = total;
        while (_underrunTimes.Count > 0 && now - _underrunTimes.Peek() > AutoGrowWindowSeconds)
        {
            _underrunTimes.Dequeue();
        }

        if (_underrunTimes.Count >= AutoGrowUnderruns && _shared.BaseLatencyMs < MaxBaseLatencyMs)
        {
            _shared.BaseLatencyMs = Math.Min(MaxBaseLatencyMs, _shared.BaseLatencyMs + AutoGrowStepMs);
            Settings.BaseLatencyMs = _shared.BaseLatencyMs;
            _underrunTimes.Clear();
            MarkDirty();
        }
    }

    private void AutoSelectSpeakers()
    {
        if (Settings.SpeakerAId is not null && Settings.SpeakerBId is not null)
        {
            return;
        }

        // Keep Windows' own order for ties (stable sort): JBLs first, then speakers that are on.
        var candidates = _backend.GetSpeakers()
            .Where(s => s.IsBluetooth)
            .OrderByDescending(s => IsJbl(s.Name))
            .ThenBy(s => s.State)
            .ToList();
        if (candidates.Count < 2)
        {
            return;
        }

        var jbl = candidates.Where(s => IsJbl(s.Name)).ToList();
        var pick = (jbl.Count >= 2 ? jbl : candidates).Take(2).ToList();
        Settings.SpeakerAId ??= pick[0].Id;
        Settings.SpeakerBId ??= pick.First(p => p.Id != Settings.SpeakerAId).Id;
        if (!Settings.SpeakerNames.ContainsKey(Settings.SpeakerAId))
        {
            Settings.SpeakerNames[Settings.SpeakerAId] = "Desk";
        }

        if (!Settings.SpeakerNames.ContainsKey(Settings.SpeakerBId))
        {
            Settings.SpeakerNames[Settings.SpeakerBId] = "Bed";
        }

        MarkDirty();
    }

    private void ApplySpeakerSettings()
    {
        var (a, b) = CurrentOffsets();
        _channels[0].OffsetMs = a;
        _channels[1].OffsetMs = b;
        _channels[0].Gain = Taper(Settings.SpeakerAId is null ? 1 : Settings.VolumeFor(Settings.SpeakerAId));
        _channels[1].Gain = Taper(Settings.SpeakerBId is null ? 1 : Settings.VolumeFor(Settings.SpeakerBId));
    }

    private (double A, double B) CurrentOffsets()
    {
        if (Settings.SpeakerAId is null || Settings.SpeakerBId is null
            || !Settings.Calibrations.TryGetValue(AppSettings.PairKey(Settings.SpeakerAId, Settings.SpeakerBId), out var cal))
        {
            return (0, 0);
        }

        return (cal.OffsetsMs.GetValueOrDefault(Settings.SpeakerAId), cal.OffsetsMs.GetValueOrDefault(Settings.SpeakerBId));
    }

    private void SetOffsets(double a, double b, string method, (double A, double B)? measured)
    {
        if (Settings.SpeakerAId is null || Settings.SpeakerBId is null)
        {
            return;
        }

        var cal = new PairCalibration
        {
            Method = method,
            When = DateTimeOffset.Now,
            OffsetsMs = { [Settings.SpeakerAId] = a, [Settings.SpeakerBId] = b },
        };
        if (measured is { } m)
        {
            cal.MeasuredLatencyMs[Settings.SpeakerAId] = Math.Round(m.A, 2);
            cal.MeasuredLatencyMs[Settings.SpeakerBId] = Math.Round(m.B, 2);
        }

        Settings.Calibrations[AppSettings.PairKey(Settings.SpeakerAId, Settings.SpeakerBId)] = cal;
        _channels[0].OffsetMs = a;
        _channels[1].OffsetMs = b;
        MarkDirty();
    }

    private string? SelectedId(SpeakerSlot slot) => slot == SpeakerSlot.A ? Settings.SpeakerAId : Settings.SpeakerBId;

    private List<string> EnabledPhoneIds() =>
        _backend.PhoneLink.GetPairedPhones().Select(p => p.Id).Where(id => !Settings.DisabledPhoneIds.Contains(id)).ToList();

    private (string Headline, string? Detail) Describe(SpeakerStatus[] speakers, bool flowing, double now)
    {
        if (_state == EngineState.Stopped)
        {
            if (Settings.SpeakerAId is null || Settings.SpeakerBId is null)
            {
                return ("Choose two speakers to get started", "Pair them with Windows first, then pick them in Speakers.");
            }

            return ("Ready", $"{speakers[0].Name} + {speakers[1].Name}");
        }

        if (_state == EngineState.Calibrating)
        {
            return ("Calibrating…", _calibrationStep);
        }

        var on = speakers.Where(s => s.Link == SpeakerLinkState.Connected).Select(s => s.Name).ToList();
        var lost = speakers.Where(s => s.Link != SpeakerLinkState.Connected).ToList();
        if (on.Count == 0)
        {
            var off = lost.Any(s => s.Problem is not null);
            return ("Looking for your speakers", off ? $"{speakers[0].Name} and {speakers[1].Name} are off or out of range" : "Connecting…");
        }

        var targets = string.Join(" + ", on);
        string headline;
        string? detail = null;
        if (_clickTrack)
        {
            headline = $"Playing the click track on {targets}";
        }
        else if (Settings.Mode == SourceMode.Phone)
        {
            var phone = _sourceName;
            if (phone is null)
            {
                headline = "Waiting for a phone";
                detail = "On your phone, open Bluetooth and choose this laptop as the speaker.";
            }
            else if (flowing)
            {
                headline = $"Playing from {phone} on {targets}";
            }
            else
            {
                headline = $"Connected to {phone}";
                detail = "Press play on the phone.";
            }
        }
        else
        {
            headline = flowing ? $"Playing this laptop's sound on {targets}" : "Waiting for sound from this laptop";
            if (!flowing)
            {
                detail = "Play something in Spotify or Apple Music on this laptop.";
            }
        }

        if (lost.Count == 1)
        {
            var s = lost[0];
            detail = s.Problem ?? $"Reconnecting to {s.Name}…";
        }

        return (headline, detail);
    }

    private void SetStep(string step)
    {
        lock (_lock)
        {
            _calibrationStep = step;
        }
    }

    private Task Delay(double seconds, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource();
        var fired = 0;
        IDisposable? handle = null;
        handle = _scheduler.SchedulePeriodic(seconds, _ =>
        {
            if (Interlocked.Exchange(ref fired, 1) == 0)
            {
                handle?.Dispose();
                tcs.TrySetResult();
            }
        });
        if (ct.CanBeCanceled)
        {
            ct.Register(() =>
            {
                handle.Dispose();
                tcs.TrySetCanceled(ct);
            });
        }

        return tcs.Task;
    }

    private void MarkDirty() => _settingsDirty = true;

    private void SaveIfDirty()
    {
        if (!_settingsDirty)
        {
            return;
        }

        _settingsDirty = false;
        try
        {
            _store.Save(Settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceError($"soundsys settings: {ex.Message}");
        }
    }
}

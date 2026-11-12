# soundsys — Design Spec

Status: approved (self-answered from the overnight brief, 2026-11-12). Decisions with rationale: [DECISIONS.md](DECISIONS.md).

## 1. Goal

A Windows 11 tray app that turns a Surface Laptop (ARM64) into a hub: one audio source in, two Bluetooth A2DP speakers out, playing in sync as if they were one speaker. Dead-simple UI. No extra hardware, no virtual audio drivers, no paid services, no phone↔laptop networking.

## 2. Users and scenarios

| Who | How they play | soundsys mode |
|---|---|---|
| Ishaan (iPhone, Apple Music) | Phone → laptop over Bluetooth | **Phone** (default) |
| Roommate (Spotify) | Spotify Connect → laptop Spotify app | **Laptop** |
| Ishaan at the laptop | Apple Music Windows app | **Laptop** |

Only one person plays at a time. Both phones stay paired. In Phone mode, whichever phone starts streaming most recently wins.

## 3. Architecture

```
Phone --A2DP--> [AudioPlaybackConnection] --> capture endpoint / loopback --+
                                                                            |
Laptop apps --> default render endpoint --> WASAPI loopback ----------------+--> Source
                                                                                  |
                                                         HistoryBuffer (shared, timestamped)
                                                          /                              \
                                       OutputChannel A (read pos A)          OutputChannel B (read pos B)
                                       resampler(SRC+drift) · gain           resampler(SRC+drift) · gain
                                                 |                                         |
                                       WASAPI render (JBL 1)                     WASAPI render (JBL 2)
```

### Projects

| Project | TFM | Contents |
|---|---|---|
| `Soundsys.Engine` | `net10.0` | All DSP, sync, state machines, abstractions, simulated devices. No Windows APIs, fully unit-testable. |
| `Soundsys.Windows` | `net10.0-windows10.0.22621.0` | WASAPI capture/render/loopback/process-loopback, AudioPlaybackConnection, endpoint enumeration + watcher, keep-awake, start-with-Windows, battery. |
| `Soundsys.App` | `net10.0-windows10.0.22621.0` (WPF) | Tray + window UI, view models, settings, update check. |
| `Soundsys.Diag` | `net10.0-windows10.0.22621.0` (console) | `soundsys-diag`: hardware checks, writes `docs/PHASE0_RESULTS.md`. |
| `Soundsys.Engine.Tests`, `Soundsys.App.Tests` | | xUnit. |

### Hardware abstraction (every touchpoint has a real and a simulated implementation)

- `IAudioSource`: pushes interleaved float frames plus a capture timestamp (laptop clock) into an `ISourceSink`. Real: `WasapiCaptureSource` (endpoint or loopback), `ProcessLoopbackSource`, `GeneratorSource`. Sim: `SimulatedSource`.
- `IOutputDevice`: pulls audio from an `IRenderTarget` on its own thread, reporting callback timestamp and frames still queued. Real: `WasapiOutputDevice`. Sim: `SimulatedOutputDevice` (configurable latency, ppm drift, stalls, disconnects).
- `IMicrophone` (an `IAudioSource` used for calibration). Real: WASAPI capture (raw mode when supported). Sim: `SimulatedRoom` mic synthesizes what the simulated speakers played: per-speaker acoustic latency, reverb, and noise.
- `IAudioBackend`: enumerates render endpoints, phones, and mics; creates sources and outputs; raises device added/removed events. Real: `WindowsAudioBackend`. Sim: `SimulatedBackend`.
- `IPhoneLink`: AudioPlaybackConnection per enabled phone. Real + sim.
- `IPowerManager` (keep-awake), `IStartupRegistration`, `IClock`.

## 4. Sync design (the hard part)

### 4.1 Shared history buffer = per-speaker delay buffers
A single-writer, multi-reader circular buffer (about 5.4 s, stereo float, at the source rate) holds the source audio. Positions are absolute 64-bit frame counts. Each speaker's delay is simply how far behind the writer its read position sits, so the delay buffers cost no extra copies and have sample resolution (the UI shows 1 ms steps). The writer publishes `(position, timestamp)` with a seqlock. If the source has a gap (capture discontinuity), the writer fills it with silence so the timeline keeps tracking real time.

### 4.2 Controlled quantity
At every render callback (time `t`, frames `q` still queued in the device), channel *i* computes its pipeline latency in source frames:

`L_i = W_est(t) − readPos_i + q·(srcRate/devRate) + filterDelay`, with `W_est(t) = W + (t − t_W)·srcRate`

Interpolating the writer position with timestamps removes the bias that would otherwise come from the phase between capture packets and render callbacks (up to ±5 ms). The target is `D + offset_i`, where `D` is the base latency (default 50 ms, grows automatically on repeated underruns) and `offset_i` is the calibrated per-speaker delay.

### 4.3 Drift control
`error = L_i − (D + offset_i)`. A low-pass filter (EMA, τ≈0.5 s) feeds a PI controller (τ≈10 s proportional, integral for steady drift) that sets the resampling ratio `nominal·(1+c)` with `|c| ≤ 1000 ppm`. If `|error| > 15 ms` (start, rejoin, offset change, stall), the read pointer jumps directly to the target with a 5 ms crossfade and the integrator resets.

### 4.4 Resampler
One stage does both sample-rate conversion (e.g. 44.1→48 kHz) and drift correction: a polyphase windowed-sinc filter (32 taps, Kaiser, 256 phases, linearly interpolated). Its 16-frame half-length gives about 0.33 ms of DSP latency. It is SIMD-vectorized with `System.Numerics.Vector<T>`, which maps to NEON on ARM64.

### 4.5 Real-time rules
The render and capture callbacks never allocate, lock, log, or do I/O. All buffers are preallocated at channel creation. Status comes out through atomically updated counters that the UI polls. This is verified by a test that runs callbacks under `GC.GetAllocatedBytesForCurrentThread()`. Real threads register with MMCSS ("Pro Audio").

### 4.6 Latency budget
DSP adds less than 1 ms (the filter's half-length; processing happens inside the render callback, so there is no extra block). The jitter margin above WASAPI's unavoidable period buffering is about 15 ms. Engine-added processing latency is therefore about 16 ms, under the 20 ms requirement. The Bluetooth hops (around 150–300 ms each) dominate and can't be avoided.

### 4.7 Calibration
1. Generate a 1.0 s exponential sine sweep (300 Hz–8 kHz, Tukey-faded). That band suits laptop mics and SBC.
2. Run calibration as the engine source: sweep at stream position `P_A`, then again at `P_B = P_A + 1.5 s`. Each channel gets a *pass window* in stream positions, so speaker A only plays sweep A and speaker B only plays sweep B.
3. Record the mic, raw mode when available to bypass voice-processing effects.
4. Use band-limited GCC-PHAT cross-correlation (FFT) against the reference. For each window, find the peak and take the *earliest* peak within 20 ms before it that is at least 50% of its height, so a strong early reflection can't win. Refine with parabolic sub-sample interpolation. Report quality as the peak-to-median ratio.
5. `d = (arrival_B − arrival_A) − 1.5 s`, so `d > 0` means B is later. New offsets are the current offsets plus the correction, normalized so the smaller one is 0. Show the numbers: absolute latency per speaker when timestamps are available, plus the applied delay.
6. If quality is too low, show a plain-English error ("Couldn't hear the Bed speaker — turn it up or move the laptop closer").
7. Save results per speaker pair (sorted endpoint IDs).

Manual fallback: a click track plays on both speakers at once (the same pipeline). A slider runs from −500 to +500 ms in 1 ms steps; negative delays Desk, positive delays Bed.

Accuracy target: within 2 ms on synthetic recordings with known delays, white noise at SNR down to 0 dB, and a reverb-like impulse response (early reflections plus a decaying noise tail, RT60 up to 0.6 s).

### 4.8 Speaker drop / rejoin
Each speaker has a `SpeakerConnection` state machine: `Idle → Opening → Active → Lost → Reopening → Active`, retrying every 1 s while the engine runs, driven by device-watcher arrivals plus a retry timer. A lost speaker stops pulling. The other speaker's thread is independent and is never paused. On reopen, the channel primes at `D + savedOffset`, so it is aligned on its first block. Target: back within 10 s of the device becoming available (the test asserts under 3 s).

### 4.9 Known physical limit
Each JBL's DAC clock versus the incoming A2DP stream is managed inside the speaker and is invisible to Windows. soundsys corrects every drift it can observe: phone clock vs laptop clock, and render endpoint vs render endpoint. If the speakers' internal buffers drift acoustically, the optional 1-hour diag drift log will show it. A periodic re-calibration feature is the follow-up (see the report).

## 5. Sources and capture

- **Phone mode.** `IPhoneLink` opens AudioPlaybackConnection for each enabled paired phone. Most recent wins: a `PhoneHandoff` state machine promotes whichever phone most recently began delivering audio. For capture it tries, in order:
  - (a) a capture endpoint that belongs to the phone (matched by container ID or name);
  - (b) loopback of the default render endpoint.
  
  Double-audio suppression is a strategy (`None`, `MuteEndpointDuringLoopback`, `MuteSourceSession`). The diag measures which one works on this machine and saves it. Nothing ever changes system volume unless the user starts the engine with that strategy chosen, and every change is restored on stop.
- **Laptop mode.** WASAPI loopback of the default render endpoint, or process loopback (`ActivateAudioInterfaceAsync`, include-process-tree) of Spotify / Apple Music when they're running. The radio carries only the two outbound streams. Suppression uses the same strategy object.
- **Safety.** If the default render endpoint is one of the two selected speakers, show a warning ("Windows is sending sound straight to Desk. Switch Windows output to the laptop speakers") because it would cause a feedback loop or double audio.

## 6. UI

WPF on .NET 10, using the built-in Fluent theme (follows system light/dark, Mica). It has a tray icon and one 400×640 window that hides to the tray on close.

- **Home:** status line, a big Start/Stop, a Phone / Laptop source switch, master volume, and one card per speaker (name, connection state, battery if exposed, volume, current offset).
- **Speakers:** pick two render endpoints from the paired Bluetooth endpoints (JBLs preselected by name) and rename them.
- **Phones:** paired phones, each with an enable toggle.
- **Sync:** Auto-calibrate with live progress and results, plus the manual slider and click-track toggle.
- **Settings:** start with Windows, auto-start when both speakers are on, keep awake, check for updates, and a hidden "Simulated devices" option (developer toggle, revealed by clicking the version label 5 times).

Every state is designed: empty (no speakers chosen), connecting, playing, one speaker dropped, calibrating, error. Errors are plain English, never stack traces. Every action is reflected within 1 s: the UI polls engine status at 10 Hz and commands are asynchronous with immediate optimistic state.

## 7. Settings and persistence
JSON at `%LOCALAPPDATA%\soundsys\settings.json`: selected speakers, names, volumes, per-pair calibration, enabled phones, mode, toggles, suppression strategy, base latency. Writes are atomic (temp file + replace).

## 8. Distribution and CI
- GitHub Actions on push/PR: build win-arm64 + win-x64, run unit tests, `dotnet format --verify-no-changes`.
- On a `v*` tag: self-contained single-file publish for both RIDs, zipped (`soundsys-<ver>-win-arm64.zip` containing `soundsys.exe` + `soundsys-diag.exe`), uploaded to a GitHub Release (pre-release when the version has a suffix).
- In-app update check: GitHub Releases API, semver compare including pre-release tags, "Update available" link. No auto-install.
- MSIX / sparse package only if AudioPlaybackConnection needs package identity (Phase 3 probe decides).

## 9. Testing
- Engine: TDD on synthetic signals. Tests cover: history buffer, resampler quality (SNR) and ratio tracking, drift controller convergence, a 1-hour simulated drift run at ±50 ppm (faster than real time on a virtual clock), calibration accuracy (sweep of delays × noise × reverb within 2 ms), reconnect timing and the other speaker's continuity, phone handoff, and zero allocations on the RT path.
- App: view-model tests against the simulated backend (state transitions, error text, 1 s responsiveness).
- Hardware: `soundsys-diag` tomorrow (see HARDWARE_TESTS.md).

# Implementation plan

Each task is small and independently testable. ✅ = done (evidence in OVERNIGHT_REPORT.md).

## Phase 1 — Spec + plan + scaffold (branch `phase1-scaffold`)
1. SPEC.md, DECISIONS.md, PLAN.md.
2. Solution `soundsys.slnx`, `Directory.Build.props` (nullable, warnings-as-errors, LangVersion latest), `global.json`, `.editorconfig`.
3. Projects: Engine, Windows, App, Diag, Engine.Tests, App.Tests. They must build for win-arm64 and win-x64.
4. Abstractions in `Soundsys.Engine/Abstractions`: `IAudioSource`, `ISourceSink`, `IOutputDevice`, `IRenderTarget`, `RenderTiming`, `IAudioBackend`, `DeviceInfo`, `IPhoneLink`, `IPowerManager`, `IStartupRegistration`, `IClock`. Then merge, so parallel work can start.

## Phase 2 — Engine (TDD, branch `phase2-engine`)
1. `HistoryBuffer`: write/read wraparound, seqlock writer state, gap fill, multi-reader. Tests.
2. `SincTable` + `Resampler`: unity ratio passthrough (delayed by the filter latency), 44.1→48 sine SNR > 80 dB, ±1000 ppm ratio tracking, consumed-frame accounting, no allocation.
3. `PiDriftController`: converges to a constant drift, clamps, resets.
4. `OutputChannel`: priming, latency estimate with timestamp interpolation, jump + crossfade, underrun handling, gain smoothing, pass windows, counters, zero allocations.
5. `Simulation`: `VirtualClock`, `SimScheduler`, `SimulatedSource` (ppm, packet jitter, dropouts), `SimulatedOutputDevice` (ppm, latency, stalls, disconnect), `SimulatedRoom` mic.
6. 1-hour drift test: capture +20 ppm, A +50 ppm, B −50 ppm, jitter, faster than real time. Assert alignment < 0.25 ms after 60 s and zero underruns.
7. `ChirpGenerator`, `Fft`, `CrossCorrelator` (GCC-PHAT), `Calibrator`. Accuracy grid: delays {0, 3.3, 57, 180, 240, 420 ms} × SNR {30, 10, 0 dB} × reverb {dry, RT60 0.3, 0.6}, all within 2 ms. Failure cases give plain errors.
8. `ClickTrackGenerator`.
9. `SpeakerConnection` state machine + reconnect test: B disconnects for 5 s, A has no glitches, B rejoins < 3 s after it returns, aligned < 0.5 ms.
10. `PhoneHandoff`: most-recent-wins logic.
11. `SyncEngine` orchestrator: start/stop, mode, speakers, volumes, offsets, calibration run, status snapshot. Uses `IAudioBackend`.
12. `SimulatedBackend` (real-time or virtual clock) for UI and tests. End-to-end simulated calibration test.
13. Settings model + JSON store (atomic), pair calibration keying.

## Phase 3 — Windows integration (branch `phase3-windows`, worktree agent)
1. `WasapiOutputDevice` (event-driven, MMCSS, float, QPC timing, padding, device-invalidated → Faulted).
2. `WasapiCaptureSource` (endpoint + loopback, discontinuity flags, QPC timestamps, raw mode option for mics).
3. `ProcessLoopbackSource` (ActivateAudioInterfaceAsync interop).
4. `EndpointEnumerator` + `MMNotificationClient` watcher; Bluetooth detection (`{0.0.0.00000000}` / enumerator name `BTHENUM`), JBL preselect.
5. `AudioPlaybackConnectionLink` (`IPhoneLink`), paired-phone listing.
6. `SuppressionStrategies` (endpoint mute / session mute with save + restore).
7. `PowerManager` (SetThreadExecutionState), `StartupRegistration` (HKCU Run), battery property read.
8. `WindowsAudioBackend`.
9. Silent probe: enumerate endpoints, check API presence, test whether AudioPlaybackConnection works unpackaged. Record the results.

## Phase 4 — UI (branch `phase4-ui`)
1. Design plan (frontend-design pass) → tokens in `Theme.xaml`.
2. Shell: tray icon, window, navigation, hide-to-tray, single instance.
3. Home, Speakers, Phones, Sync, Settings views + view models.
4. Every state designed and reachable in simulated mode. App.Tests view-model tests.
5. Update checker (GitHub Releases, semver).
6. Screenshots from simulated mode (rendered off-screen, silent).

## Phase 5 — GitHub
1. `ci.yml` (push/PR): format check, build arm64+x64, tests.
2. `release.yml` (tag v*): publish self-contained single-file for both RIDs, zip, GitHub Release (pre-release if suffix).
3. README with Mermaid diagram, install, first-time setup, troubleshooting (SmartScreen).
4. Green CI → tag `v0.1.0-alpha`.

## Phase 6 — Hardware kit
1. `soundsys-diag` checks: env/API, phone link + capture path, suppression, distinct tones via mic, 10-min stutter (phone vs laptop), real chirp calibration, `--drift-hour`, power-cycle reconnect. Waits by detection. Writes `docs/PHASE0_RESULTS.md`.
2. `docs/TOMORROW.md`, `docs/HARDWARE_TESTS.md`.

## Phase 7 — Report
`docs/OVERNIGHT_REPORT.md` with evidence. Push.

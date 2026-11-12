# Overnight report: 2026-11-12

**Bottom line:** the engine, the Windows integration, the simulated end-to-end flow, the diag kit, CI and docs are done, and every result below is backed by CI or local evidence. **Before anything else tomorrow, read [TOMORROW.md](TOMORROW.md) step 0.** Smart App Control is enforcing on this laptop and may block unsigned soundsys builds; I didn't change that setting.

<!-- UI_SECTION -->

## "Done for tonight" checklist

| Requirement | Status | Evidence |
|---|---|---|
| All engine tests green | ✅ | 136 engine tests + 69 Windows tests pass on **win-x64 and win-arm64** (CI runs listed below) |
| Simulated drift, 1 h at ±50 ppm | ✅ | 3600 s simulated in 14.7 s; max misalignment **0.0042 ms** over 353,982 blocks; 0 underruns / jumps / glitches |
| Calibration accuracy within 2 ms | ✅ | 36-case grid (delays 0–420 ms × SNR 30/10/0 dB × RT60 0/0.3/0.6 s): max error **0.001 ms**. Reflection 1.6× louder than the direct path: 60.001 ms (true 60). Simulated room through the whole engine: 59.05 ms (true 59), second pass 0.14 ms |
| Reconnect | ✅ | The dropped speaker plays again **250 ms** after it returns, aligned to **0.028 ms**; the other speaker has 0 glitches and 0 underruns |
| Builds for arm64 + x64 | ✅ | CI builds natively on `windows-11-arm` and `windows-latest`, and publishes self-contained single-file `soundsys.exe` + `soundsys-diag.exe` for each RID |
| Runs end-to-end in simulated mode | <!-- SIM_STATUS --> | <!-- SIM_EVIDENCE --> |
| CI green; v0.1.0-alpha pre-release | <!-- RELEASE_STATUS --> | <!-- RELEASE_EVIDENCE --> |
| soundsys-diag + TOMORROW.md ready | ✅ | `soundsys-diag` runs all 9 checks. CI runs it end-to-end on simulated devices on every push (8 PASS, suppression correctly SKIPPED in simulation). It also ran locally. [TOMORROW.md](TOMORROW.md), [HARDWARE_TESTS.md](HARDWARE_TESTS.md) |
| OVERNIGHT_REPORT.md | ✅ | This file |

## What was built

| Phase | Branch → main | What |
|---|---|---|
| 0 | `main` | .NET 10 SDK 10.0.404 (arm64) installed via winget; first commit (README stub, .gitignore, MIT) |
| 1 Spec + plan | `phase1-scaffold` | [SPEC](SPEC.md), [PLAN](PLAN.md), [DECISIONS](DECISIONS.md); solution + six projects; hardware abstractions (`IAudioSource`, `IOutputDevice`, `IRenderTarget`, `IAudioBackend`, `IPhoneLink`, …) |
| 2 Engine (TDD) | `phase2-engine` | Shared timestamped history buffer (per-speaker delay = read-position lag); polyphase sinc resampler (SRC + drift in one stage); gain-scheduled PI drift controller; `OutputChannel` with timestamp-interpolated latency, jumps with crossfade, underrun-vs-pause classification; source router with gap fill and timeline continuity; sweep / GCC-PHAT calibrator; reconnect state machine; phone handoff; `SyncEngine` orchestrator; simulation (virtual-time scheduler, drifting / stalling / disconnecting speakers, phones, a room with a mic); settings; update checker |
| 3 Windows | `phase3-windows` (+ review fixes) | WASAPI render/capture/loopback (event-driven, MMCSS, QPC timestamps, allocation-free loops), process loopback (`ActivateAudioInterfaceAsync`), AudioPlaybackConnection link, endpoint catalog with Bluetooth detection that works on Qualcomm's offload stack, playback suppression with crash recovery, keep-awake, start-with-Windows, battery, the silent probe |
| 4 UI | `phase4-ui` | <!-- UI_ROW --> |
| 5 GitHub | `phase5-ci` | CI on every push/PR (format, build + test on x64 and arm64, simulated diag, publish smoke); tag `v*` → self-contained zips → GitHub Release (pre-release when the version has a suffix); README with Mermaid diagram |
| 6 Hardware kit | `phase6-diag` | `soundsys-diag`: 9 checks, waits by detection, writes `docs/PHASE0_RESULTS.md`; simulated operator for unattended runs; [TOMORROW.md](TOMORROW.md), [HARDWARE_TESTS.md](HARDWARE_TESTS.md) |

## Evidence

<!-- CI_RUNS -->

Numbers from the CI TRX logs (identical on x64 and arm64):

- **1-hour drift.** Phone +20 ppm, Desk +50 ppm, Bed −50 ppm, packet and callback jitter.
  - Alignment from 60 s to 1 h: max 0.0042 ms, mean 0.0032 ms.
  - Learned corrections: −29.8 / +69.9 ppm (truth −30 / +70).
- **Disturbances.** Inbound dropouts of 30, 120 and 400 ms, plus a 60 ms stall on one speaker: max 0.014 ms outside 1.5 s windows. The other speaker had 0 glitches.
- **Sources.** A 44.1 kHz source to 48 kHz speakers: max 0.0066 ms. Pause/resume: max 0.0007 ms, 0 underruns.
- **Switching sources** 8 times (music ↔ test signal): alignment < 0.25 ms, no controller saturation.
- **Simulated `soundsys-diag`** (CI, every push):
  - tones heard 71/63 dB above noise;
  - phone audio in 0.9 s;
  - calibration residual 0.1 ms;
  - rejoin 0.2 s;
  - drift log spread ≤ 0.4 ms.
- **Silent real-hardware probe** ([WINDOWS_PROBE.md](WINDOWS_PROBE.md)):
  - AudioPlaybackConnection works unpackaged (selector and enumeration); no phone was paired, so the full test waits for tomorrow;
  - default-output loopback delivers 10 ms packets continuously;
  - process loopback works;
  - the QPC time base is confirmed;
  - the capture loop allocates nothing in steady state.

## Bugs found and fixed tonight (root cause first)

1. **Calibration test hang in CI.** Under xUnit's SynchronizationContext, `ConfigureAwait(false)` continuations were queued to the thread pool while the test advanced virtual time past them. Found with blame-hang plus a timeout message that named the stuck step. Fix: drive simulated time with no sync context, which matches the app's threads.
2. **Start-up acquisition transient** (0.34 ms for about 40 s). This was a cold-start integrator; I added gain scheduling, and it now settles to about 0.05 ms.
3. **Source switches nudged the speakers apart** (up to 1.3 ms; the controllers saturated at −1000 ppm). The router reset its timeline on every switch. Fix: keep the timeline continuous by filling the switch gap with silence.
4. **Code review of the engine.** Calibration sweeps were truncated after fix 3: the windows were placed before the gap fill. Calibration now starts a fresh timeline segment, and sweep quality went from 71/61 to 76/73 dB. Also fixed:
   - container-following could pick the wrong device;
   - 10 Hz status polling enumerated COM endpoints and read battery each time (now cached);
   - calibration killed a running test signal.
5. **Code review of the Windows integration:**
   - a shutdown deadlock in the endpoint watcher;
   - a use-after-free on the process-loopback activation timeout;
   - container lookups that never expired, or retried a 3 s query forever;
   - a raw COM exception escaping when mute failed;
   - phones stuck in an error state;
   - a leaked connection;
   - a crash-unmute marker deleted before the unmute happened.
6. **Seqlock reader could spin forever** if the capture thread was preempted mid-publish. It's now time-bounded at 1 ms, so a real-time thread can't stall.
7. **A flaky allocation test on arm64.** Tiered JIT/OSR allocated once inside the measured window; the test now measures a fully warmed second pass.

## Decisions

There are 40+ one-line decisions with rationale in [DECISIONS.md](DECISIONS.md). The key ones:
- **WPF on .NET 10** rather than WinUI 3: native ARM64, plain `dotnet publish`, built-in Fluent theme.
- **Unpackaged zip distribution**; a sparse package only if the phone test shows AudioPlaybackConnection needs identity.
- **One shared history buffer**, where each speaker's delay is its read lag.
- **One resampler stage** for SRC and drift together.
- **Phone capture path and double-audio suppression are measured by the diag, not assumed.**
- **I didn't touch Smart App Control.**

**Skills used:** the `superpowers:*` skills aren't installed in this session, so I followed their intent manually (spec → plan → TDD → review → verify → merge). `frontend-design` isn't installed either; I read its SKILL.md from the marketplace and applied it (design doc: [UI_DESIGN.md](UI_DESIGN.md)). The installed `code-review` skill was run on the engine and the Windows layer, and its findings were verified and fixed.

## Known risks (what tomorrow's hardware run will settle)

1. **Smart App Control** may block the unsigned app. Your options are in TOMORROW.md step 0.
2. **Two A2DP sinks at once on Qualcomm's Bluetooth offload stack.** This is the key unknown, and the `tones` check answers it.
3. **Phone capture path.** Does AudioPlaybackConnection audio show up as a capture endpoint, or only on the default output (loopback)? With Qualcomm offload, Windows may play it in the DSP where loopback can't see it. The `phone` check answers this.
4. **Double audio.** If capture is loopback-based and this driver's loopback is post-mute, the laptop speakers will play along. The `suppression` check tries a session mute and then an endpoint mute, and reports honestly. A fix without a virtual cable may not exist on this driver.
5. **Package identity for AudioPlaybackConnection.** There's no sign it's needed: every static entry point works unpackaged. The sparse-package plan is in WINDOWS_PROBE.md if the phone test shows otherwise.
6. **3 streams on one radio** (phone mode) may stutter. The `stutter` check compares against 2-stream Laptop mode, and the engine raises its buffer automatically after repeated underruns.
7. **Each speaker's internal DAC clock vs. the incoming A2DP stream is invisible to Windows.** soundsys corrects everything it can observe. The optional `--drift-hour` log will show whether the speakers drift acoustically. If they do, the next step is periodic quiet re-calibration.
8. **The calibration results are on idealised synthetic signals.** Real rooms, speaker responses, the SBC codec and laptop-mic processing will be worse. The target is 2 ms, and synthetic error was 0.001 ms, so there's plenty of margin. The engine requests raw-mode capture to bypass mic effects where supported.
9. **The `WasapiOutputDevice` render path has never run on real hardware** (it would have been audible). The `tones` check is its first run.
10. **Minor:** in real-time simulation, after a calibration both drift controllers briefly show a common-mode correction transient. Alignment stays within 0.4 ms.

## Not done, and why

<!-- NOT_DONE -->

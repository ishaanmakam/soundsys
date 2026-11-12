# Decisions log

One line per decision, with the rationale. Newest at the bottom of each section.

## Process / tooling
- **Skills available:** none of the `superpowers:*` skills are installed in this session (the plugin data dir is empty), so I followed their intent by hand: spec → plan → TDD → review → verify → merge. `frontend-design` exists in the official marketplace but isn't installed; I read its SKILL.md and applied it manually for the UI. The installed `code-review` / `simplify` skills are used for phase reviews.
- **Per-phase skill use:** P1 spec/plan = brainstorming + writing-plans (manual, self-answered). P2 engine = test-driven development (manual). P3 Windows = systematic-debugging for any interop failures. P4 UI = frontend-design (read from the marketplace) + manual design pass. Each phase ends with a `code-review` skill review, a verification-before-completion pass (build/test output in the report), and a branch merge into main.
- **Branching:** one feature branch per phase, merged with `--no-ff` into main and pushed. Engine and Windows work ran in parallel (the Windows agent in its own git worktree) because both depend only on the abstraction interfaces committed first.
- **No attribution trailers on commits**, because the user's Claude Code settings set `attribution` to empty.

## Platform
- **UI framework: WPF on .NET 10 (not WinUI 3).** First-class win-arm64, plain `dotnet build`/`publish` (single-file, self-contained) with no MSIX or XAML-compiler friction in CI, and a built-in Fluent theme since .NET 9 with system light/dark and Mica. Tray via WinForms `NotifyIcon`. WinUI 3 would need Windows App SDK runtime packaging plus a third-party tray library.
- **TFM `net10.0-windows10.0.22621.0`, minimum OS 10.0.19041:** gives CsWinRT projections for `Windows.Media.Audio.AudioPlaybackConnection` (introduced in 2004/19041) without extra packages.
- **Audio interop: NAudio.Wasapi for the CoreAudio COM wrappers only.** I wrote my own event-driven render/capture loops so the real-time path is allocation-free and I control timing (QPC timestamps, padding). Process loopback (`ActivateAudioInterfaceAsync`) is custom interop because NAudio doesn't cover it.
- **Engine targets plain `net10.0`:** no Windows dependency, so all sync logic is testable anywhere with fakes.
- **Test framework: xUnit 2.9.** Stable, well-known, and runs under `dotnet test` on CI.

## Sync engine
- **One shared timestamped history buffer with a per-speaker read position (instead of a ring per speaker):** the read-position lag *is* the per-speaker delay buffer. That gives zero-copy fan-out, sample resolution, and lets the read pointer jump instantly when an offset changes.
- **Interpolate the writer position by timestamp:** removes up to ±5 ms of alignment bias caused by the phase between capture packets and render callbacks.
- **Single resampler stage for SRC + drift:** a polyphase windowed sinc (32 taps, 256 phases). This means one filter (0.33 ms latency) instead of two. Vector<T> maps to NEON on ARM64.
- **PI control of pipeline latency (EMA-filtered, ±1000 ppm clamp) plus a jump-with-crossfade above 15 ms:** the slow loop handles clock drift inaudibly; the jumps handle start, rejoin, and offset edits instantly.
- **Base latency 50 ms default, auto-grows +10 ms after 3 underruns in 30 s (cap 150 ms):** BT capture packets are bursty; robustness beats the last 10 ms.
- **Fill capture gaps with silence:** keeps the shared timeline locked to real time, so both speakers stay aligned through inbound dropouts.
- **Calibration: 1 s exp sweep 300 Hz–8 kHz, band-limited GCC-PHAT, earliest-strong-peak picking, parabolic refinement:** sharp peaks under reverb, robust to laptop-mic bandwidth and noise suppression. The 1.5 s gap between the speaker A and speaker B sweeps tolerates latency differences up to ±1 s.
- **Calibration routes through the normal pipeline using per-channel "pass windows":** what gets measured is exactly what plays music, including the engine's own buffering.
- **Calibration saved per speaker pair keyed by the sorted endpoint IDs:** swapping which is A/B can't corrupt it.
- **Rejoin primes at base latency plus the saved offset:** aligned on the first block with no slewing, and the other speaker is untouched (independent threads, shared buffer is read-only for them).

## Capture / sources
- **Phone capture path is probed, not assumed:** first look for a phone capture endpoint, then fall back to loopback of the default render endpoint. Whether Windows exposes the AudioPlaybackConnection stream as a capture endpoint can only be confirmed with a real phone, so the diag decides and saves the result.
- **Double-audio suppression is a pluggable strategy (`None` / `MuteEndpointDuringLoopback` / `MuteSourceSession`) chosen by the diag:** whether loopback is pre- or post-mute is driver-dependent. Every change is restored on stop, and nothing touches volume tonight.
- **Laptop mode prefers process loopback of Spotify/Apple Music when running, else endpoint loopback:** keeps system sounds out of the speakers when possible.

## Windows integration (Phase 3)
- **Own `[ComImport]` + `[PreserveSig]` declarations for the streaming interfaces (IAudioClient/2, render/capture clients); NAudio is used only for enumeration, properties, endpoint volume, sessions and notifications:** the RT loops get raw HRESULTs with no exception allocation, and I control WAVEFORMATEXTENSIBLE and raw mode.
- **Every stream lives on its own MTA thread, which opens the client, runs the loop and releases COM. `Start` uses a two-step handshake (opened → caller runs Prepare/OnFormat → started):** this avoids STA marshaling from a WPF caller, and Prepare/OnFormat stay off the RT path while open failures still throw synchronously as `AudioDeviceException`.
- **Render prefill is silence (`AUDCLNT_BUFFERFLAGS_SILENT`), not engine audio:** keeps `RenderTiming.QueuedFrames` truthful from the first callback. A 6 s event stall counts as a disconnect: Bluetooth links can die without `DEVICE_INVALIDATED`.
- **Loopback is polled every 5 ms with a high-resolution waitable timer. Endpoint capture and process loopback are event-driven:** the probe showed process-loopback events arrive every 10 ms even in silence.
- **Process-loopback completion handler uses source-generated COM (`[GeneratedComClass]`, implements `IAgileObject`), and `GetActivateResult` is called through the vtable:** built-in CCWs for non-public classes were a risk I couldn't test under SAC. The probe confirmed activation works.
- **Bluetooth detection = bus enumerator → vendor-stack hint → device-container lookup:** this Snapdragon laptop exposes Bluetooth endpoints through Qualcomm's `QCASD` ACX driver, so checking only for `BTHENUM` misses them (see WINDOWS_PROBE.md). The container lookup also supplies the real device name.
- **Speaker list collapses Bluetooth duplicates per container and hides HFP when A2DP exists. Recommend matching saved speakers by ContainerId:** one physical speaker can have several endpoint IDs across driver stacks.
- **AudioPlaybackConnection: `StartAsync` on enable, plus one ~10 s `OpenAsync` attempt with backoff (`AutoOpen`). No periodic background paging:** paging an absent phone ties up the shared radio that also carries both speaker streams. `Release` re-creates and re-starts without opening, so the phone can come back on its own.
- **Suppression only toggles mute, never volume. It leaves a marker file so a crash can be undone at next start, and never "restores" a mute the user had set themselves.**
- **Status polled at 10 Hz from atomic engine counters:** meets "every action reflected within 1 s" without RT-thread events.
- **Simulated-devices toggle hidden behind 5 clicks on the version label:** it's a dev option that shouldn't confuse normal use.
- **Settings JSON in %LOCALAPPDATA% with atomic replace:** no registry clutter, survives crashes.

## Distribution
- **Release artifact = zip of self-contained single-file `soundsys.exe` + `soundsys-diag.exe` per RID:** no installer prerequisites, works unpackaged, and the README explains SmartScreen "Run anyway".
- **Version comes from the git tag (`-p:Version=`) in CI:** one source of truth.

## Environment findings
- **Smart App Control (SAC) is in enforcement mode on the hub laptop** (`VerifiedAndReputablePolicyState=1`). It blocks every unsigned binary we build (`0x800711C7`, CodeIntegrity event 3077), so local test runs and local launches of `soundsys.exe` / `soundsys-diag.exe` fail. **Decision: I did not disable or circumvent SAC (it's a security control the user didn't ask me to change). All tests are verified on GitHub Actions (x64 + arm64 runners, no SAC). Builds and `dotnet format` still run locally.** The user has to choose tomorrow between turning SAC off and getting the binaries signed. See TOMORROW.md step 0.
- **WSL is not installed** (enabling it needs admin + reboot), so it's not usable as a local test runner tonight.

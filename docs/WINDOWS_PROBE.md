# Windows probe results

Silent probe of the development laptop (Snapdragon, Windows 11 26H1 build 28000.3501, ARM64), run on 2026-11-12 during the overnight Phase 3 session. Nothing was rendered, no microphone was opened, no mute or volume was changed, and `AudioPlaybackConnection.OpenAsync` was never called.

**How it was produced:** `soundsys-diag probe --out <file>` (win-arm64 Release build, unpackaged). The coordinator reported that Smart App Control is in enforcement mode on this laptop, which blocks unsigned binaries. Even so, the freshly built `soundsys-diag.exe` ran without being blocked at 01:20–01:25, so the raw report below comes from the C# probe itself. I collected supplementary facts with Windows PowerShell 5.1, a signed host: MMDevices registry properties, PnP devices per container, and the WinRT `AudioPlaybackConnection` / `DeviceInformation` calls. Those are marked *(PowerShell)*. If SAC blocks the probe on a later run, the same facts can be regathered that way.

Re-run: `soundsys-diag probe --out <file>` (add `--no-phone-start` to skip the phone `TryCreateFromId` + `StartAsync` experiment). Write to a new file rather than over this one, which also holds the findings below.

## Findings

### 1. AudioPlaybackConnection without package identity: API reachable, phone experiment not possible tonight

- The process has **no package identity** (`GetCurrentPackageFullName` → `APPMODEL_ERROR_NO_PACKAGE`).
- `AudioPlaybackConnection.GetDeviceSelector()` **works unpackaged**, and so does `DeviceInformation.FindAllAsync(selector)`. The selector is the A2DP **source** service (`{0000110A-…}`) on audio-class device interfaces that are enabled.
- **No paired phone was found** (0 devices). The only paired Bluetooth devices are "Ishaan's Beats Studio Pro" (headphones, an A2DP sink) and a "Wireless Controller" *(PowerShell: AEP query for paired classic Bluetooth devices)*. A query for A2DP-source interfaces that also included disabled ones *(PowerShell)* returned 0 as well. So the planned `TryCreateFromId` + `StartAsync` experiment on a phone **could not be run**, and the probe correctly reports "not possible, no paired phone".
- `TryCreateFromId("soundsys-probe-not-a-device")` throws `InvalidCastException`, HRESULT `E_NOINTERFACE (0x80004002)`. The result was identical from CsWinRT (.NET 10) and from Windows PowerShell's .NET Framework projection, and identical for an audio-endpoint interface ID that isn't a phone *(PowerShell)*. This looks like the "not an A2DP-source interface" path. It is **not** a package-identity error: such an error would surface as `APPMODEL_ERROR_NO_PACKAGE (0x80073D54)`, `E_ACCESSDENIED`, or a failure in `GetDeviceSelector` itself.
- **Conclusion:** there is no sign that AudioPlaybackConnection needs package identity. Every static entry point works unpackaged, and open-source unpackaged Win32 tools that use this API exist (for example "AudioPlaybackConnector"). This still has to be **confirmed with a real phone**. Tomorrow's hardware run should pair the phone, then run `soundsys-diag probe`, which performs `TryCreateFromId` + `StartAsync` + `Dispose` (never `OpenAsync`) on the first phone.
- **If it turns out to need identity** (for example `StartAsync`/`OpenAsync` failing with `0x80073D54` or `E_ACCESSDENIED`), the fix is a **sparse package** ("packaging with external location"). This keeps the unpackaged exe/zip distribution and adds identity:
  1. An `AppxManifest.xml` with `<Identity>`, `<uap10:AllowExternalContent>true</uap10:AllowExternalContent>`, `<rescap:Capability Name="runFullTrust"/>`, and an `<Application Executable="soundsys.exe" uap10:TrustLevel="mediumIL" uap10:RuntimeBehavior="win32App">`. No special capability is documented for AudioPlaybackConnection. Add `<DeviceCapability Name="bluetooth"/>` only if a test shows it is needed.
  2. Package it with `makeappx pack /nv` and sign it with `signtool`. The certificate must be trusted on the machine, so release builds need a real code-signing certificate. Self-signed only works on dev machines, and SAC in enforcement mode needs proper signing anyway.
  3. An app-side manifest entry (`<msix xmlns="urn:schemas-microsoft-com:msix.v1" publisher=… packageName=… applicationId=…/>` in the exe's embedded Win32 manifest).
  4. At first run, register with `PackageManager.AddPackageByUriAsync(msixUri, new AddPackageOptions { ExternalLocationUri = installDir })`, and unregister on uninstall.

  The alternative is a full MSIX package. That needs the same signing, and its friction is the reason it was ruled out in DECISIONS.md.

### 2. On this laptop, Bluetooth audio goes through Qualcomm's ACX offload driver (QCASD), not BTHENUM

- The Beats headphones have **two** A2DP render endpoints. One is a stale Microsoft-stack endpoint (enumerator `BTHENUM`, hardware instance `{1}.BTHENUM\{0000110B-…}`). The other is a Qualcomm offload endpoint (enumerator **`QCASD`**, instance `{1}.QCASD\A2DPSPEAKERHEADSETSPEAKER-3003329455\…`, interface "Qualcomm(R) Aqstic(TM) ACX A2dp Speaker Headset" in the registry). The HFP endpoints are also QCASD ("…Bluetooth Hands Free Speaker/Mic Audio Device") *(PowerShell: MMDevices registry)*.
- **Detecting Bluetooth by `PKEY_Device_EnumeratorName == "BTHENUM"` alone would miss every Bluetooth speaker on this machine.** `EndpointClassifier` therefore uses three checks, in this order:
  1. A Bluetooth bus enumerator or instance prefix (`BTHENUM`, `BTHHFENUM`, `BTHLEDEVICE`, `BTHLE`).
  2. A vendor-stack hint: the instance or interface mentions "Bluetooth" or "A2DP".
  3. A **device-container lookup** (Windows.Devices.Enumeration): the endpoint's `PKEY_Device_ContainerId` container holds a PnP device whose instance ID starts with a Bluetooth bus. For the Beats container `{287a0343-…}` that includes `BTHENUM\Dev_F873DFD5B103…` *(PowerShell)*. This check works for any driver stack. The same lookup also gives the device's real name ("Ishaan's Beats Studio Pro").
- Endpoints of the computer itself use container `{00000000-0000-0000-FFFF-FFFFFFFFFFFF}` and are never classified as Bluetooth.
- The speaker list collapses the duplicates to **one entry per Bluetooth device**: it prefers the current vendor-stack endpoint, drops HFP endpoints when an A2DP one exists, and hides inactive duplicates when the device has an active endpoint. Display names come from the container ("Ishaan's Beats Studio Pro") or have the driver suffix stripped ("Speakers (2- Qualcomm(R) … Audio Device)" → "Speakers"). Identical names get " (2)" (two identical JBLs).
- **Risk for the engine and settings:** a Bluetooth speaker can have several endpoint IDs over time (Microsoft stack vs vendor stack, re-pairing). `AudioEndpointInfo.ContainerId` is stable per physical device, so saved speaker selections and calibrations should match by ContainerId first and endpoint ID second.
- The JBL speakers aren't paired with this laptop yet, so the Qualcomm stack's behavior with two simultaneous A2DP sinks (dual-A2DP) is still untested. That is the central hardware question for tomorrow.

### 3. Capture endpoints and the phone capture path

- The capture endpoints are the built-in "Microphone Array" (Active, 48 kHz float stereo mix format), SoundWire headset mics (unplugged or not present), and the Beats HFP mic (QCASD, classified Bluetooth + hands-free).
- No phone is paired, so it is **unknown whether an AudioPlaybackConnection session exposes a capture endpoint**. `AudioPlaybackConnectionLink.CreateSource` tries a capture endpoint matched by ContainerId or name first, then falls back to loopback of the default render endpoint.
- **Risk specific to this hardware:** with Qualcomm's ACX audio offload, Windows might render the inbound A2DP stream in the DSP. If so, it may not pass through the audio engine where loopback can see it. The hardware tests should check "phone plays → loopback peak > 0" first.

### 4. Loopback and process loopback both work unpackaged; timestamps are QPC

- **Default-endpoint loopback** (the built-in speakers, 48 kHz float stereo): 101 packets / 48,480 frames in about 1 s of capture, which is **10 ms packets delivered continuously even while nothing plays** (peak 0.0). On this build, silence produces a steady stream of zero packets rather than gaps. The first packet carries `DATA_DISCONTINUITY`, which is normal at stream start. Polling every 5 ms with a high-resolution waitable timer kept up. The 1.29 s wall time includes about 0.28 s of stream open.
- **Process loopback** (`VAD\Process_Loopback`, include-tree, own PID): activation succeeded with the source-generated `[GeneratedComClass]` completion handler (agile, `IAgileObject`). An explicit 48 kHz float stereo format was accepted. Event callbacks arrived every 10 ms (30 packets / 0.31 s) even with the target silent. Requires Windows build 20348 or newer, so older Windows falls back to endpoint loopback (`FallbackSource` in `WindowsAudioBackend`).
- **Timestamps:** the QPC frequency is 10 MHz. The first packet's QPC timestamp, converted with `value × 1e-7`, was 10–16 ms older than `QpcClock.Now`, which is consistent with one 10 ms period plus wake-up latency. This confirms that WASAPI's `u64QPCPosition` and `Stopwatch` share a time base.
- **Real-time allocation:** the capture loop allocated **48 bytes once per run**: the same 48 B for 30 and for 101 packets, which points to first-call interop warm-up. Steady state is allocation-free. The render loop is built the same way but could not be run tonight (no audible output allowed).

### 5. Other facts

- Active render endpoints are the built-in Speakers (default; 48 kHz float stereo) and a Dell monitor over HDMI (48 kHz float stereo). There are many stale NotPresent HDMI and SoundWire endpoints; the speaker list correctly hides them.
- **Battery:** `DEVPKEY_Bluetooth_Battery` read **50%** for the Beats while they were off. Windows keeps the last reported value, so the UI should label battery as "last known" while the speaker is Unavailable.
- Mix formats of every active endpoint are 48 kHz / float32 / stereo, so on this machine the requested format (float stereo at the mix rate) involves no conversion at all.

## Raw probe output

Generated 2026-11-12 01:25 by `soundsys-diag probe` (silent).

### Environment

- OS: Microsoft Windows 10.0.28000 (26H1, build 28000.3501)
- OS architecture: Arm64; process architecture: Arm64
- .NET: .NET 10.0.15
- Package identity: none (unpackaged process; APPMODEL_ERROR_NO_PACKAGE)
- QPC frequency: 10000000 Hz

### Render endpoints (all states)

| Name | State | Bluetooth | Hands-free | Enumerator | Container | Mix format | ID |
|---|---|---|---|---|---|---|---|
| Speakers | Active | False | False | QCASD | {00000000-0000-0000-ffff-ffffffffffff}  | 48000 Hz, 2 ch, Float32 | `{0.0.0.00000000}.{0936b7db-c788-4189-94aa-d5b55d64b018}` |
| Digital Output | NotPresent | False | False | QCASD | {c4b6661a-93e8-543d-94c0-c39bed942e3c} (Dell SE2726HGS (HDMI1)) | — | `{0.0.0.00000000}.{1a346b86-05b7-4697-af8a-b0662498d3aa}` |
| Ishaan’s Beats Studio Pro | Unplugged | True | False | BTHENUM | {287a0343-c739-58a9-bcaf-c0f6d9535b34} (Ishaan’s Beats Studio Pro) | — | `{0.0.0.00000000}.{1c92e70f-0758-4baa-b97d-5a1e58b474f1}` |
| DELLSE2726HGS | NotPresent | False | False | QCASD | {e176f00b-e3a3-5ca2-b174-ca3151d226cb} (Generic Monitor) | — | `{0.0.0.00000000}.{1fab51ca-c4bc-4ddf-beeb-05e1442cf2e0}` |
| Headset Earphone on SoundWire Device | Unplugged | False | False | SOUNDWIRE | {baa4216a-fa00-5f58-a252-a45313555a2a}  | — | `{0.0.0.00000000}.{2fbe2e3f-9ca0-4a5e-b414-bfe74243b1e4}` |
| Digital Output | NotPresent | False | False | QCASD | {86c03cc7-19d7-5e0e-8a36-6ba00b1f516e} (Digital Output (8- Qualcomm(R) Aqstic(TM) ACX External Display Audio Device)) | — | `{0.0.0.00000000}.{31a048bc-bae2-49bb-852b-f3eac8efe853}` |
| DELLSE2726HGS | NotPresent | False | False | QCASD | {c4b6661a-93e8-543d-94c0-c39bed942e3c} (Dell SE2726HGS (HDMI1)) | — | `{0.0.0.00000000}.{6c5db8a6-6eec-47c5-bf64-1678bafbe0c9}` |
| Speakers | NotPresent | False | False | QCASD | {00000000-0000-0000-ffff-ffffffffffff}  | — | `{0.0.0.00000000}.{893347aa-e168-4e24-8e65-cd17143403f8}` |
| Headset Earphone on SoundWire Device | NotPresent | False | False | SOUNDWIRE | {df560a43-f9cd-56be-90cb-ebc426838f32}  | — | `{0.0.0.00000000}.{ad96a653-b042-476f-942f-7f4c010ad8a2}` |
| DELLSE2726HGS | Active | False | False | QCASD | {c4b6661a-93e8-543d-94c0-c39bed942e3c} (Dell SE2726HGS (HDMI1)) | 48000 Hz, 2 ch, Float32 | `{0.0.0.00000000}.{d4a6295a-cbbd-404e-bdd3-5015e46cbf02}` |
| Digital Output | NotPresent | False | False | QCASD | {92577f0d-e219-586f-8bb4-4e3acbb6d48d} (Generic Monitor) | — | `{0.0.0.00000000}.{d819d500-da18-4cf0-bcb4-468fc51d2b65}` |
| DELLSE2726HGS | NotPresent | False | False | QCASD | {549162dc-e6dd-5b8c-bf84-f10458ab955d} (Generic Monitor) | — | `{0.0.0.00000000}.{dd93e75c-c5ee-4eb2-bdca-2e63d7558fd2}` |
| Digital Output | NotPresent | False | False | QCASD | {e176f00b-e3a3-5ca2-b174-ca3151d226cb} (Generic Monitor) | — | `{0.0.0.00000000}.{de55e84e-df90-42e3-97e6-796d38286777}` |
| Ishaan’s Beats Studio Pro | Unplugged | True | False | QCASD | {287a0343-c739-58a9-bcaf-c0f6d9535b34} (Ishaan’s Beats Studio Pro) | — | `{0.0.0.00000000}.{e0989faf-f399-43f5-8d78-b6cec2862c94}` |
| Digital Output | NotPresent | False | False | QCASD | {549162dc-e6dd-5b8c-bf84-f10458ab955d} (Generic Monitor) | — | `{0.0.0.00000000}.{fbbf952c-4979-425d-b184-caca04b99d7a}` |

### Capture endpoints (all states)

| Name | State | Bluetooth | Hands-free | Enumerator | Container | Mix format | ID |
|---|---|---|---|---|---|---|---|
| Headset Microphone on SoundWire Device | NotPresent | False | False | SOUNDWIRE | {64a2e865-4ac6-5403-bd15-829483de08a0}  | — | `{0.0.1.00000000}.{0d3683de-4bd3-43dd-9b4f-5d974bf062d9}` |
| Microphone Array | Active | False | False | QCASD | {00000000-0000-0000-ffff-ffffffffffff}  | 48000 Hz, 2 ch, Float32 | `{0.0.1.00000000}.{878f32e8-318f-46db-82c9-d9fb34dbebf8}` |
| Ishaan’s Beats Studio Pro | Unplugged | True | True | QCASD | {287a0343-c739-58a9-bcaf-c0f6d9535b34} (Ishaan’s Beats Studio Pro) | — | `{0.0.1.00000000}.{8b2e6afb-8667-4470-97b1-d733bfebf75e}` |
| Microphone Array | NotPresent | False | False | QCASD | {00000000-0000-0000-ffff-ffffffffffff}  | — | `{0.0.1.00000000}.{b9f74083-9198-4cd8-a2fc-5933399fb860}` |
| Headset Microphone on SoundWire Device | Unplugged | False | False | SOUNDWIRE | {4c46d7fa-3a18-5dd6-b8cd-072275d3f3c8}  | — | `{0.0.1.00000000}.{cbee633b-5a2a-4abe-b80e-11310f30a94a}` |

### What soundsys lists

Speakers:
- Speakers — Active, Bluetooth=False, battery=n/a (`{0.0.0.00000000}.{0936b7db-c788-4189-94aa-d5b55d64b018}`)
- DELLSE2726HGS — Active, Bluetooth=False, battery=n/a (`{0.0.0.00000000}.{d4a6295a-cbbd-404e-bdd3-5015e46cbf02}`)
- Ishaan’s Beats Studio Pro — Unavailable, Bluetooth=True, battery=50% (`{0.0.0.00000000}.{e0989faf-f399-43f5-8d78-b6cec2862c94}`)

Microphones:
- Microphone Array — Active (`{0.0.1.00000000}.{878f32e8-318f-46db-82c9-d9fb34dbebf8}`)

Default render endpoint: Speakers (`{0.0.0.00000000}.{0936b7db-c788-4189-94aa-d5b55d64b018}`)

### AudioPlaybackConnection

- GetDeviceSelector: OK — `System.Devices.DevObjectType:=8AND System.Devices.InterfaceClassGuid:="{6994AD04-93EF-11D0-A3CC-00A0C9223196}"AND System.DeviceInterface.Bluetooth.ServiceGuid:="{0000110A-0000-1000-8000-00805F9B34FB}"AND System.Devices.InterfaceEnabled:=System.StructuredQueryType.Boolean#True`
- Paired devices that can stream to this PC (A2DP source): 0
- TryCreateFromId(non-device ID) threw: InvalidCastException HRESULT E_NOINTERFACE (0x80004002): Specified cast is not valid.
- TryCreateFromId + StartAsync experiment: not possible, no paired phone.

### Process loopback (own process, 0.3 s)

- OK: format 48000 Hz, 2 ch, 30 packets / 14400 frames in 0.31 s, discontinuities 0, peak 0.0000, first-packet timestamp lag 10.0 ms, bytes allocated in capture loop 48, target PID 20652

### Default-endpoint loopback capture (1 s)

- OK: format 48000 Hz, 2 ch, 101 packets / 48480 frames in 1.29 s, discontinuities 1, peak 0.0000, first-packet timestamp lag 13.2 ms, bytes allocated in capture loop 48


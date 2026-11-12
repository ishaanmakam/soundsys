# Hardware test checklist

This mirrors `soundsys-diag`. Each row is one check: `--only <id>` runs it alone. Results go to `docs/PHASE0_RESULTS.md`.

| ☐ | id | Check | How it's verified (automatically) | Pass criteria | You do |
|---|---|---|---|---|---|
| ☐ | `environment` | Devices present | Lists render/capture endpoints, Bluetooth classification, paired phones, default output, plus the full silent probe | ≥ 2 Bluetooth speakers, ≥ 1 phone, a mic | Nothing |
| ☐ | `speakers` | Both speakers connected | Polls endpoint state until both selected JBLs are Active; reads battery if Windows exposes it | Both Active within 3 min | Turn them on |
| ☐ | `tones` | Both play **at the same time** | Opens two WASAPI render streams at once (880 Hz on A, 1320 Hz on B) and records the laptop mic; Goertzel level per tone vs. noise | Each tone ≥ 12 dB above noise | Listen |
| ☐ | `phone` | Phone → laptop works (AudioPlaybackConnection) | Starts the engine in Phone mode and waits for captured audio; records whether a phone capture endpoint appeared or loopback is used; any Windows error text | Audio flowing within 4 min | Choose the laptop as speaker, press play |
| ☐ | `suppression` | No double audio | Measures the default output by loopback while the phone plays. If the phone is audible there, tries (1) muting only the phone's session, then (2) muting the laptop output, checking each time that soundsys still captures; saves the first that works | Laptop output silent, or a working fix found | Nothing (brief mute/unmute) |
| ☐ | `stutter` | 3-stream vs. 2-stream stability | N minutes (default 10) of phone in + 2 out, then N minutes of laptop-generated test music (2 out only); counts engine underruns, inbound gaps, silent seconds, latency auto-grow | Phone mode ≤ 0.2 underruns/min (WARN ≤ 1/min) | Keep the phone playing |
| ☐ | `calibration` | Real chirp calibration | Engine auto-calibration (sweep A, sweep B, mic, GCC-PHAT), applied; then two measure-only passes give the residual | Both speakers heard; residual ≤ 2 ms | Laptop where you sit, quiet |
| ☐ | `reconnect` | Speaker power-cycle | Plays test music; waits for Bed to be lost, then for it to come back; times Windows reconnect → soundsys playing; watches Desk throughout; re-measures sync with the saved delay | Rejoin ≤ 10 s, Desk never interrupted, sync still ≤ 2 ms | Turn Bed off, then on |
| ☐ | `drift` | 1-hour drift log (optional, `--drift-hour`) | Measures the speaker offset every 5 min for 60 min and logs drift-controller corrections | Offset moves ≤ 2 ms over the hour | Leave it running |

## Silent checks (safe at night)

```powershell
dotnet run --project src\Soundsys.Diag -c Release -- probe --out probe.md       # environment only, nothing audible
dotnet run --project src\Soundsys.Diag -c Release -- --simulated --quick        # the whole flow on simulated devices
```

## Manual fallbacks

- **Calibration fails ("couldn't hear …")**: Sync tab → *Play click track*. Move the slider until the two clicks merge into one. Negative values delay Desk, positive values delay Bed.
- **Phone capture doesn't work**: use Laptop mode. Play from Spotify (Spotify Connect from either phone) or the Apple Music app on the laptop.
- **One speaker can't stream alongside the other**: note which models, then try with the speakers closer to the laptop and other Bluetooth devices off.

# Tomorrow: first run on real hardware

About 45 minutes in total, most of it hands-off. You need both JBL speakers (charged), your iPhone, and the Surface. It plays real audio, so do it when it's OK to make noise.

## Step 0: Smart App Control (read first)

Smart App Control is **on (enforcing)** on this laptop. It blocks some unsigned programs outright, with no "Run anyway".
- Last night it blocked one freshly built test DLL but let `soundsys-diag.exe` run, so it decides file by file.
- **Building and running from source (below) is the most likely to work.**
- The downloaded release zip (marked as coming from the internet) is the most likely to be blocked.

If you see *"An Application Control policy has blocked this file"* or the program silently doesn't start:
- **Option A (no system change):** try once more after a rebuild (`dotnet build -c Release`). Sometimes the next build is allowed.
- **Option B:** turn Smart App Control off: Windows Security → App & browser control → Smart App Control settings → Off. ⚠️ On many Windows 11 builds it can't be turned back on without resetting Windows. It's your call; I didn't change it.
- **Option C (later):** code-sign the builds. The free option is SignPath's open-source program (needs an application); paid options exist. Not set up.

## Step 1: Pair the speakers (5 min)

1. Windows: **Settings → Bluetooth & devices → Add device → Bluetooth**.
2. JBL: hold the Bluetooth button until it blinks fast, then click it in the Windows list. Do this for **both** speakers.
3. Leave both speakers on.

## Step 2: Pair the iPhone with the laptop (2 min)

1. Keep the laptop's Bluetooth settings page open, so the laptop is discoverable.
2. iPhone: **Settings → Bluetooth** → tap the laptop's name → confirm the code on both.

## Step 3: Check the Windows output (30 s)

Click the speaker icon in the taskbar and make sure the output is the **laptop speakers**, *not* a JBL. soundsys sends to the JBLs itself. Set the laptop volume to about 30 %.

## Step 4: Run one command (≈ 35 min, mostly waiting)

Open **Terminal** and run:

```powershell
cd $HOME\Documents\soundsys
git pull
dotnet run --project src\Soundsys.Diag -c Release
```

`--minutes 3` makes the stutter test 3 minutes per mode instead of 10, and `--skip stutter` leaves it out entirely.

It tells you what to do in yellow **▶** lines and waits until it *detects* that you did it. You don't press any keys. In order:

| # | You'll be asked / hear | What you do |
|---|---|---|
| 1 | Nothing (it lists your devices) | n/a |
| 2 | ▶ Turn on both speakers | Turn them on (already on is fine). |
| 3 | Two tones at once for 3 s (one per speaker) | Listen: both should play. The laptop mic checks this too. |
| 4 | ▶ On your phone, choose this laptop as the speaker, then press play | iPhone: Control Center → AirPlay icon (top-right of the music tile) → pick the laptop. Play a song. |
| 5 | Laptop speakers may mute and unmute briefly | Nothing; it's testing how to stop the laptop from playing along. Everything is restored afterwards. |
| 6 | ▶ Keep music playing on the phone for 10 minutes | Leave it. Then 10 minutes of soft test music from the laptop. |
| 7 | Short sweeps on Desk, then Bed (3 times) | Put the laptop where you usually sit. Keep quiet for about 20 s. |
| 8 | ▶ Turn OFF the Bed speaker → ▶ turn it back ON | Do it. Desk should keep playing the whole time. |

At the end it prints a summary and writes **`docs/PHASE0_RESULTS.md`**. Please commit and push it (`git add docs/PHASE0_RESULTS.md; git commit -m "Phase 0 hardware results"; git push`) so I can read it. Calibration and the double-audio fix it finds are saved into soundsys's settings automatically.

Press **Ctrl+C** at any time to stop. It un-mutes or un-does anything it changed.

**Optional, any time later:** `dotnet run --project src\Soundsys.Diag -c Release -- --only drift --drift-hour` logs sync for an hour, with a short sweep every 5 minutes.

## Step 5: Use the app

```powershell
dotnet run --project src\Soundsys.App -c Release
```

Press **Start**, pick the laptop on your phone, and play.
- **Sync** tab → **Auto-calibrate** whenever you move things around.
- Closing the window keeps it running in the tray.
- Your roommate: Spotify on the laptop (or Spotify Connect to the laptop) with soundsys switched to **Laptop**.

## What "good" looks like

- Tones: both speakers heard (each roughly 20 dB or more above the noise).
- Phone: audio arrives within a minute of picking the laptop.
- Stutter: 0–2 underruns in 10 minutes in Phone mode, and fewer in Laptop mode.
- Calibration: the remaining offset after correction is ≤ 2 ms.
- Reconnect: Bed plays again within 10 s of Windows reconnecting it, and Desk never stops.

## If something fails

| Result | Likely meaning | Next step |
|---|---|---|
| Tones: only one speaker heard | The Bluetooth radio/driver can't run two A2DP streams at once | Try with the speakers closer; send me the report (it's the key hardware question). |
| Phone: "Windows wouldn't let soundsys receive audio … 0x80073D54" or access denied | AudioPlaybackConnection needs package identity after all | I'll add a sparse package (plan in WINDOWS_PROBE.md). |
| Phone: picked the laptop, but no audio arrives | Qualcomm's audio offload may play the phone audio where Windows can't capture it | Send the report; Laptop mode still works. |
| Suppression: FAIL | The laptop speakers will play along in Phone mode | Turn the laptop volume to 0 in Windows **after** checking that capture still works, or use Laptop mode. |
| Stutter: many underruns in Phone mode only | One radio is carrying 3 streams | Use Laptop mode for long sessions; soundsys raises its buffer automatically. |

# soundsys UI design

Produced in a frontend-design pass (the plugin skill isn't installed; I read its SKILL.md and applied it by hand).

## Subject, audience, job
- **Subject:** a small utility that makes two cheap Bluetooth speakers behave like one. Its world is home hi-fi: the receiver on the shelf, warm backlit dials, VU needles, two cones playing as a pair.
- **Audience:** two college roommates. One uses Apple Music on an iPhone, the other Spotify. They open it from the tray, press one button, and get on with their evening. They don't care about milliseconds unless something sounds off.
- **Primary job:** answer "is it playing, from whom, on which speakers?" at a glance, and offer one big Start/Stop. Everything else (volume, sync, phones) is secondary and should feel quiet.

## Direction: "two cones, one sound" on a Windows 11 shell
It must look native: Fluent, Mica, follows system light/dark, Segoe UI Variable. The identity comes from one signature element and a hi-fi-amber signal colour. It does not come from re-skinning Windows.

### Signature element (the one bold thing)
**The Start control is two rings, one per speaker.**
- When stopped, they sit side by side, slightly apart, outlined in muted ink.
- When playing, they slide together until they overlap into a single lens-shaped pair, filled with the amber signal colour. "Two become one" is the whole product in a single image.
- Each ring also *is* that speaker's live state:
  - amber: playing
  - outlined and slowly breathing: connecting
  - dashed in alert colour: lost
  - grey: off
- The slide is the only orchestrated motion in the app (260 ms, ease-out) and happens only on state change. It respects `SystemParameters.ClientAreaAnimation` (reduced motion: no slide, just a state swap).
- The caption under the rings is the action verb: "Start" / "Stop" / "Calibrating…".

Everything around it stays quiet and disciplined. There are no cards with shadows and no gradient washes. Speakers are rows separated by spacing, not boxed tiles.

### Colour tokens
These are deliberately warm and receiver-like, not default Windows blue, and avoid the stock AI palettes (no cream + terracotta, no black + acid green).

| Token | Light | Dark | Use |
|---|---|---|---|
| `Ink` | `#1C1B1A` | `#F1EFEA` | primary text |
| `InkMuted` | `#64615C` | `#A8A49C` | secondary text, idle rings |
| `Signal` | `#B86E00` | `#F2A93B` | playing / active, the merged rings, slider fill |
| `SignalSoft` | `#33B86E00` | `#33F2A93B` | ring fill, focus halo |
| `Alert` | `#B3261E` | `#FF8A80` | lost speaker, errors |
| `Hairline` | `#14000000` | `#1FFFFFFF` | separators only |

The background is the Mica window backdrop (system). Nothing paints over it except a subtle error/notice strip.

### Type
- One family: **Segoe UI Variable**, the native Windows 11 face (the brief asks for the Fluent look, so this is the deliberate choice, not a default).
- Two optical sizes:
  - **Display** (`Segoe UI Variable Display`) for the headline only, Semibold 24/30.
  - **Text** for everything else: 14/20 body, 12/16 labels.
- Numbers (ms, %, battery) use tabular figures so they don't jitter as they update.
- Headline casing is sentence case. No ALL-CAPS labels, and no eyebrow labels above sections.
- Section titles on secondary pages are plain Semibold 14.

### Layout: main window, 400 × 660, left-aligned text, centred signature
```
┌────────────────────────────────────┐
│ soundsys                    —  ✕  │  Mica title bar
│                                    │
│ Playing from Ishaan's iPhone       │  headline (Display 24, Ink)
│ on Desk + Bed                      │
│ Bed is off or out of range         │  detail (14, InkMuted or Alert)
│                                    │
│            ◯◯                      │  the two rings, ~180 px wide
│           Stop                     │  verb caption
│                                    │
│  Phone │ Laptop                    │  segmented source switch
│                                    │
│  🔊 ───────────●──────── 80        │  master volume
│                                    │
│  ● Desk    JBL Flip 6     🔋 80%   │  speaker row: state dot, name, device, battery
│     ────────●──────      +59 ms    │  its volume, its sync offset
│  ● Bed     JBL Charge 5   🔋 45%   │
│     ──────────●────                │
│                                    │
│  Speakers   Phones   Sync   ⚙      │  text tab bar; the current tab is underlined in Signal
└────────────────────────────────────┘
```
Secondary pages replace the middle region and keep the tab bar:
- **Speakers:** for "Speaker 1" and "Speaker 2", a dropdown of endpoints (Bluetooth first, JBL first, unavailable ones marked "off") and a name box.
- **Phones:** a list of paired phones, each with a toggle and a live state ("Playing", "Connected", "Not connected"). Explainer line: "On your phone, open Bluetooth and choose this laptop."
- **Sync:**
  - "Auto-calibrate" (accent button) with live step text, then the result in plain words plus the numbers (Desk 232 ms · Bed 291 ms → Desk waits 59 ms).
  - Manual: a slider from −500 to +500 ms (label "Bed later →" / "← Desk later") with a value readout and a "Play click track" toggle.
- **Settings:** toggles for Start with Windows, Start automatically when both speakers are on, and Keep the laptop awake while playing; Check for updates (button and result); the version label (5 clicks unlocks Developer: "Use simulated devices" and simulation controls).

## Every state
| State | Headline | Rings | Notes |
|---|---|---|---|
| Empty (no speakers chosen) | "Choose two speakers to get started" | both grey, button disabled | detail links to the Speakers tab |
| Ready (stopped) | "Ready" + "Desk + Bed" | apart, outlined | |
| Connecting | "Looking for your speakers" | outlined, breathing | |
| Playing | "Playing from X on Desk + Bed" | merged, amber | |
| Waiting for phone | "Waiting for a phone" | merged, outlined | detail: how to connect |
| One speaker dropped | "Playing … on Desk" | one amber, one dashed alert | detail: "Bed is off or out of range" in Alert |
| Calibrating | "Calibrating…" | both breathing | step text; other controls disabled |
| Error | the headline stays | | a slim Alert strip with the plain-English message and a dismiss ✕; never a stack trace |
| Warning (Windows default output is a chosen speaker) | | | an InkMuted strip with an "Open sound settings" link |

## Tray
- The icon is two small rings, drawn at runtime: amber when playing, ink when stopped, alert when a speaker is lost.
- The tooltip is the headline.
- Menu: Open soundsys · Start/Stop · Source ▸ Phone/Laptop · Quit.
- Closing the window hides it to the tray; the first time, a balloon says "soundsys is still running in the tray".

## Review against the brief (generic-default check)
- *Card grid for speakers?* That's the default SaaS card kit. I changed it to rows with a status dot, because two speakers aren't a dashboard.
- *Blue accent?* That would be the Windows default. I changed it to receiver amber, which is grounded in hi-fi gear and stays distinct on Mica in both themes.
- *Big round play button?* That's generic. I changed it to the two-ring "become one" control, which is specific to this product's promise.
- *Monospace for ms numbers?* That's a listed tell. I changed it to tabular figures in Segoe UI Variable.

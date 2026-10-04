# RC-N1 Bridge spec

A Windows app that makes a DJI RC-N1 remote show up as an Xbox 360 controller. It replaces the Python v1 (now on the `legacy` branch) with a native C# app, fixes v1's connection bugs, and adds support for the RC's extra buttons.

## Goals

- Looks and behaves like a Windows 11 app: Mica, system title bar, light/dark following Windows, tray icon.
- Plug in, power on, fly. Anything that stops that from happening is explained on screen with the fix.
- Never leaves the virtual controller holding a stale stick position.
- Every RC-N1 input usable in games: both sticks, gimbal dial, shutter/record, Fn, RTH, photo/video toggle, C/N/S switch.
- One download, no Python, no admin rights, and as little antivirus friction as we can manage (see [Antivirus and trust](#antivirus-and-trust)).

Out of scope for 4.0: other DJI remotes (RC-N2, RC 2, DJI RC), DualShock/DualSense output, macOS/Linux.

## Stack

| Piece | Choice | Why |
|---|---|---|
| Runtime | .NET 10 (LTS, supported to Nov 2028) | .NET 9 support ends Nov 2026 |
| UI | WPF + [WPF-UI](https://github.com/lepoco/wpfui) 4.3 (MIT) | Windows 11 controls, Mica, NavigationView, tray via `WPF-UI.Tray` |
| MVVM | CommunityToolkit.Mvvm 8.4 | Source-generated properties and commands |
| Serial | Raw Win32 (overlapped CreateFile/ReadFile) | System.IO.Ports has a background event thread that can crash the process when a USB serial device is unplugged |
| Virtual pad | Nefarius.ViGEm.Client 1.21 + ViGEmBus driver | Same driver v1 used through vgamepad |
| Tests | xUnit | Core only; the App project stays thin |

The ViGEmBus driver is retired upstream. It still installs and works on Windows 11, but nobody is fixing it. If it ever stops working, the output layer is behind an interface (`IGamepadOutput`) so it can be swapped.

## Solution layout

```
RCN1Bridge.slnx
Directory.Build.props       shared settings, version and file metadata
global.json                 pins the .NET 10 SDK
nuget.config                nuget.org only
src/
  RCN1Bridge.Core/          no WPF, no ViGEm; everything testable lives here
    Protocol/               DUML framing, CRCs, packet builder, stream parser, stick and button decoders
    Device/                 Win32 serial, port discovery, connection engine and poller
    Input/                  calibration, deadzone, expo, smoothing, dial thresholds
    Output/                 IGamepadOutput
    Diagnostics/            message stats, button capture, latency, log ring buffer
    Mapping/                profiles and bindings (milestone 4, not written yet)
  RCN1Bridge.App/           WPF shell: pages, controls, ViGEm output, tray, settings
tests/
  RCN1Bridge.Core.Tests/    unit tests plus engine tests against a fake RC
tools/
  PollBench/                measures stick and button rates per polling setup on a real RC
```

## How data flows

```
 SerialPort ──bytes──> Reader thread ──> DumlParser ──frames──> FrameRouter
                                                                  │
                     ┌────────────────────────────────────────────┤
                     v                                            v
              StickDecoder / ButtonDecoder                  InspectorTap (if open)
                     │
                     v
               RcState (raw)  ──> Calibration + curves ──> Mapper ──> IGamepadOutput.Submit
                     │
                     └──> latest-snapshot slot <── UI timer (60 Hz) reads it
```

Threads:

1. **Reader thread** (one per connection, dedicated, not thread-pool). It blocks on `SerialPort.BaseStream.Read` into a reusable buffer, feeds the parser, and runs everything from decode to `Submit` inline. One thread from wire to game means no handoff delay and no shared mutable stick state.
2. **Poll timer**. It sends the stick poll, and the sim-enable command until the first stick frame arrives (see [Polling](#polling)).
3. **UI thread**. It reads an immutable snapshot that the reader thread publishes with `Volatile.Write`, so there are no locks and nothing to tear.
4. **Device watcher**. It listens for `WM_DEVICECHANGE` and rescans ports only when a device arrives or leaves. v1 polled every 2 s on the UI thread.

The gamepad output is created once when the app starts and lives until it exits. Disconnects, pauses and timeouts send a neutral report instead of destroying the pad, so the game keeps the same controller slot.

## Protocol reference (DUML v1)

Everything here comes from v1's working code. Anything not proven on hardware is marked unknown.

```
offset  size  field
0       1     0x55 start byte
1       2     length (low 10 bits) | protocol version << 10 (version 1 -> byte 2 has 0x04)
3       1     CRC-8 of bytes 0..2, seed 0x77
4       1     sender   (0x0A = PC)
5       1     receiver (0x06 = RC)
6       2     sequence number, little endian
8       1     command type / flags (0x40 on our requests)
9       1     command set (0x06 = RC)
10      1     command id
11      n     payload
len-2   2     CRC-16 of bytes 0..len-3, seed 0x3692, little endian
```

Both CRC tables are copied from v1 unchanged. Minimum frame length is 13 (no payload).

Commands we send:

| Set / ID | Payload | Purpose |
|---|---|---|
| 06 / 24 | `01` | Enable simulator mode so the RC streams stick data |
| 06 / 01 | none | Poll stick channels |
| 06 / 27 | none | Poll buttons and flight mode switch, every 33 ms once sticks are flowing |

Stick messages (offsets are from the start of the frame, values are u16 LE, raw range 364 to 1684, centre 1024). Confirmed on an RC-N1 on 3 Oct 2026, which sends both:

| Message | Channels | Offsets | Notes |
|---|---|---|---|
| 06 / 26, 21 B | RH, RV, LV, LH (AETR order) | 11, 13, 15, 17 | Unsolicited push, about 54/s. v1's "21 B" path was reading this. Used only when 06 / 01 stops. |
| 06 / 01, 38 B | RH, RV, LV, LH, gimbal dial, slots 5-7 | Byte 11, then 8 slots of (tag, u16) from byte 12 | Poll reply, about 120/s. Slots 5-7 read 1024 at rest. Sender is 0x0E, not 0x06. |

Buttons: 06 / 27 reply, 58 B, from 0x06. A big-endian u16 at frame bytes 28-29 (the rest of the frame is little-endian):

| Bits | Meaning |
|---|---|
| 0x3000 | Flight mode: 0x0000 S, 0x1000 N, 0x2000 C. Confirmed. |
| 0x0002 | Fn. Confirmed. |
| 0x0004 | Photo/video mode toggle. Confirmed. |
| 0x0060 | Capture (top right photo/record button; two bits, likely half and full press). Confirmed. |
| 0x0080 | Return to home. Confirmed. |

All confirmed on an RC-N1, 4 Oct 2026.

Not carrying buttons on an RC-N1 (tested 4 Oct 2026): slots 5-7 of the 38 B frame stay at 1024 for every control, and 06 / 05, 4C, 50 and 51 get no reply from either chip or address. The RC also pushes 06 / 1E (19 B, about 2/s), its battery: payload `28 0A 00 00 64 00`, where 0x64 = 100%. Address 0x0E is the HD transmission MCU (ground side), which sends the stick replies and 06 / 26.

Open:
- Whether any of this differs between RC-N1 units paired with different drones.

The app decodes by command set and id first, then by frame size. A frame with an unexpected size for its command shows up in the Inspector and counts as "unrecognised". It never moves the sticks.

### Parser rules

The parser is a small state machine over a 4 KB ring buffer. It doesn't wait for reply-sized reads.

1. Skip bytes until `0x55`.
2. Wait for 4 bytes. Check the CRC-8. Check the length is between 13 and 1023 (the length field is 10 bits). If either check fails, drop one byte and go back to step 1.
3. Wait for the full length. Check the CRC-16. If it fails, drop one byte and go back to step 1.
4. Emit the frame as a `ReadOnlySpan<byte>` over the buffer. No copy, no allocation.

It never throws on bad input. A fuzz test feeds it random bytes and spliced frames to prove that.

### Polling

- Polls go out on a timer, never in response to junk bytes. The timer starts at 200 Hz and sends a poll right away whenever a stick reply arrives, so the rate follows how fast the RC can answer.
- Two stick polls in flight (`PollsInFlight`), buttons (06 / 27) every 33 ms. The RC answers one request at a time, so button polls cost stick rate. Measured on an RC-N1, 4 Oct 2026: 1 in flight with buttons every 20 ms gave 93 stick and 44 button updates/s; 2 in flight with 33 ms gives 129 and 27, with no bad frames. A missing reply frees its slot after 20 ms.
- Until the first stick frame arrives, sim-enable is resent every 3 s. After 10 s without one, the UI shows the "unplug, plug in, then power on" hint.
- The sequence number increments per packet, starting from a random value.

## Connection states

```
NoDriver ─(driver found)─> Searching ─(port found & opened)─> WaitingForData ─(first stick frame)─> Live
    ^                          ^                                     │                              │
    │                          └──────────(port lost / error)────────┴──────────────────────────────┘
    └─(ViGEmBus missing at any time)
Live ─(no stick frame for 250 ms)─> Stalled ─(frame)─> Live
```

- On entering Stalled, Searching or NoDriver, send a neutral report immediately.
- Port loss is detected by an exception on the reader thread, or by a device-removed notification. Either way, the reader thread closes its own port and exits; the UI thread never touches the port.
- Each connection gets its own `CancellationTokenSource`. A late error from an old connection can't affect a new one.

### Port discovery

Query `Win32_PnPEntity` (System.Management) off the UI thread for COM devices. Match by DJI's USB vendor ID first (believed to be `0x2CA3`, confirm on hardware), then fall back to a description containing "For Protocol", as v1 does. Keep the full list for the Home checklist, so users can see what is plugged in.

## Input processing

All of this runs per axis, in this order. It's pure functions in Core with unit tests.

1. **Calibration.** Per-axis min, centre and max from the wizard. Defaults are 364 / 1024 / 1684. The halves either side of centre are scaled separately so an off-centre stick still reaches full range both ways.
2. **Deadzone.** Radial for each stick (applied to the stick's distance from centre), scaled-radial so output starts at 0 just outside the zone. Default 3%.
3. **Expo.** `out = expo * in^3 + (1 - expo) * in`. Default 0.
4. **Rate.** Multiplier, 50 to 100%.
5. **Smoothing.** Optional one-pole filter, off by default. It adds latency, and the UI says so.
6. **Output.** Scaled to `short`, clamped to -32768..32767.

The gimbal dial gets calibration, deadzone and an optional threshold for button bindings. The v1 threshold, 32000 of 32767 (about 97.7%), is kept as the default.

## Mapping and profiles

A profile is a JSON file in `%APPDATA%\RCN1Bridge\profiles\`. Profiles can be imported and exported as plain files, so people can share them on Nexus.

```json
{
  "name": "Cyberpunk FPV",
  "stickMode": 2,
  "axes": {
    "LeftX":  { "source": "LeftH",  "invert": false, "deadzone": 0.03, "expo": 0.0, "rate": 1.0 },
    "LeftY":  { "source": "LeftV",  "invert": false, "deadzone": 0.03, "expo": 0.0, "rate": 1.0 },
    "RightX": { "source": "RightH", "invert": false, "deadzone": 0.03, "expo": 0.0, "rate": 1.0 },
    "RightY": { "source": "RightV", "invert": false, "deadzone": 0.03, "expo": 0.0, "rate": 1.0 }
  },
  "buttons": [
    { "input": "DialUp",   "output": "Y" },
    { "input": "DialDown", "output": "B" }
  ]
}
```

- Inputs: LeftH, LeftV, RightH, RightV, Dial, DialUp, DialDown, Capture, Fn, Rth, PhotoVideo, ModeC, ModeN, ModeS. Buttons that haven't been decoded yet show as "not detected yet" and can't be bound.
- Outputs: every Xbox 360 axis, both triggers, every button, D-pad directions, or "Switch profile to ...". That last one is how the C/N/S switch can pick a tuning profile.
- Stick mode 1/2/3/4 swaps sticks and axes in one setting instead of four remaps.
- The shipped profiles are "Cyberpunk FPV" (default) and "Generic". Shipped profiles are read-only, and **Duplicate** makes an editable copy.
- Buttons bound to a switch position (ModeC/N/S) fire as a 60 ms pulse when the switch moves, plus once when the RC connects. A held D-pad direction would sit on the game's own D-pad actions the whole time.

## Cyberpunk FPV mod integration

The C/N/S switch drives the Drone mod's flight setup, going from calm to fast:

| Switch | Mod flight mode | Mod profile |
|---|---|---|
| C | Standard (auto-level, DJI-like) | n/a |
| N | Acro | Cinematic |
| S | Acro | Racing |

Each position is a mod setting, so players can point any of them at Custom instead.

**Bridge side.** The "Cyberpunk FPV" profile binds ModeC / ModeN / ModeS to D-pad Left / Down / Right as pulses (see above). Nothing else is needed.

**Mod side** (Drone repo, `r6/scripts/Drone/FPVHotkeys.reds` and `FPVSystem.reds`). Today the mod has a Tab toggle between Acro and Standard, and the profile can only be changed in Mod Settings. It needs:
- Three new `EInputKey` hotkeys: "Switch position C / N / S", defaulting to `IK_Pad_DigitLeft` / `IK_Pad_DigitDown` / `IK_Pad_DigitRight`.
- Three new settings: what each position selects (Standard, Acro Racing, Acro Cinematic, Acro Custom), with the defaults from the table.
- Each hotkey **sets** the mode and profile rather than toggling, writes them back to `FPV_FlightMode` / `FPV_Profile` so the next session starts in the switch's position, calls `OnSettingsChanged()`, and shows the existing mode banner.
- While FPV is active, the mod consumes those three D-pad presses, so the game doesn't also use a consumable or similar.

Things to check before building the mod side:
- Whether Codeware's `Input/Key` callback fires for pad buttons at all. The hotkeys are compared against `KeyInputEvent.GetKey()`, and no hotkey binds a pad button yet. If it doesn't, read the D-pad as game actions in `FPVInput.OnAction` instead.
- What the D-pad does in base Cyberpunk when FPV is off. A switch flip while walking around still sends the pulse. If that turns out to be annoying, the bridge gets a "send switch pulses" toggle in the profile.

**Existing conflict to fix.** In Standard mode, any held key flag zeroes all stick axes (`FPVInput.reds:231-236`), and the game's Jump/Crouch (A/B) set those flags. v1 maps the gimbal dial's down position to B, so rolling the dial down in Standard mode kills the sticks while it's held. Options: the mod ignores the A/B flags when a pad is in use, or the Cyberpunk FPV profile moves the dial off B. Decide when the mod side is built.

## UI

WPF-UI `FluentWindow` with Mica, a `NavigationView` on the left and five pages. The mockup linked above is the reference.

**Home**
- An InfoBar whose colour and text come from the connection state. Each state has a plain-language message and one action button (Retry, Scan again, Install driver).
- Two stick views: circle, dot, a hollow "ghost" dot when the raw position is outside the circle (kept from v1), and a deadzone ring. Drawn with a `DrawingVisual`, redrawn only when the snapshot changes.
- Gimbal dial bar, C/N/S indicator, a chip per button.
- Stats: update rate, median input-to-submit time, bad-frame count, virtual pad slot.
- When nothing is connected, a setup checklist replaces the sticks: driver, DJI USB driver, controller found (with the port list), stick data. It updates live as things change.

**Mapping**: profile picker, one row per input with a live activity dot, output picker and invert. **Learn** jumps to the row of the next input that moves.

**Tuning**: per-stick sliders (deadzone, expo, rate, smoothing), a live response curve with the current stick position plotted on it, and the calibration wizard showing raw min, centre and max per axis.

**Inspector**: one row per (command set, id) with size, rate and latest payload. Bytes that changed in the last 500 ms are highlighted. Also: pause, "only changes", save a capture to a `.bin` file with timestamps, and name a byte (saved to `%APPDATA%\RCN1Bridge\inspector-names.json`). The tap costs nothing while the page is closed.

**Settings**: start with Windows, keep running in tray when closed, reconnect automatically, centre sticks when the signal drops (on, and the timeout is fixed at 250 ms), theme, driver status, copy diagnostics, about and check for updates.

Tray: left-click opens the window, right-click opens a menu (Open, Pause output, Profile submenu, Quit). Profile changes and disconnects raise a Windows notification only while the window is hidden.

Accessibility: every control is keyboard reachable with visible focus. Status is never shown by colour alone (icon plus text). Respects the Windows text size setting.

## Fixes over v1

| v1 problem | v2 behaviour |
|---|---|
| Pad keeps last stick values after disconnect | Neutral report on every exit from Live; pad lives for the whole app |
| Short length field crashes the reader (`IndexError`) | Parser bounds-checks and never throws |
| No CRC checks; any 21/38 B frame moves the sticks | CRC-8 and CRC-16 checked; decode by command id |
| One poll per junk byte | Timer-driven polls, at most two in flight |
| New virtual pad on every reconnect | One pad per app run |
| Port scan and `sleep(0.5)` on the UI thread | Device-change notifications, scan off the UI thread |
| Old connection can signal the new one | Per-connection cancellation |
| RC powered off but still plugged in shows "Connected" | 250 ms watchdog moves to Stalled |
| Two drifting copies (CLI and UI) | One app; a `--headless` switch runs with just the tray icon |

## Performance targets

- Under 1 ms median from last byte read to `Submit`, measured with `Stopwatch` and shown on Home.
- Zero allocations per frame on the reader thread. A unit test checks this with `GC.GetAllocatedBytesForCurrentThread`.
- Under 1% CPU while live, near 0 with the window hidden (UI timer stops when minimised).
- Starts in under a second on a mid-range PC.

## Antivirus and trust

No build can be guaranteed clean on every scanner. These are the things that make false positives rare and quick to clear.

**How it's built and shipped**
- No packers, no UPX, no obfuscation, no compressed single-file bundles, nothing that unpacks itself to `%TEMP%`. A self-contained WPF single-file build has to extract its native DLLs at runtime, which is exactly what heuristics look for, so we don't ship it.
- Release download: `RCN1Bridge-4.x.y-win-x64.zip` holding one framework-dependent single-file `RCN1Bridge.exe` (7.7 MB, 2.9 MB zipped) and the licence. Needs the .NET 10 Desktop Runtime; Windows offers the download if it's missing. Nothing is extracted at runtime.
- VirusTotal, unsigned, 4 Oct 2026: the first single exe (33 MB, versioned Windows SDK target, description "RCN1Bridge") scored 3/69, all machine-learning engines (Arctic Wolf, SecureAge, Trapmine). After targeting plain `net10.0-windows` and setting the description to "RC-N1 Bridge" it scored 1/71. Microsoft, Kaspersky, Bitdefender, ESET and the other major engines were clean both times. VirusTotal still tags it `overlay`, because a single-file .NET exe is a small launcher with the app appended. If that ever costs more detections, the fallback is the framework-dependent folder build (exe plus DLLs, no overlay).
- Target plain `net10.0-windows`. A versioned Windows SDK target pulls in the 25 MB WinRT projection (`Microsoft.Windows.SDK.NET.dll`), which the app never uses.
- Full file version info on the exe (product, company, description, version) from `Directory.Build.props`, plus a manifest with `asInvoker`. Unsigned exes with no metadata are a common heuristic hit.
- Built by GitHub Actions from a tagged commit, with GitHub artifact attestations and SHA-256 hashes in the release notes, so anyone can check the zip came from the source.
- Code signing through the [SignPath Foundation](https://signpath.org) free open-source programme. This is the biggest single win against SmartScreen and AV heuristics. It needs a public repo under an OSI licence (Apache 2.0 qualifies), releases built by CI with their GitHub Actions integration, and a code signing policy page in the repo. Apply once milestone 6's pipeline exists, since they want to see the CI build. Until approval, releases are unsigned and SmartScreen will show "unknown publisher" for a while.

**What the app does at runtime**
- Never asks for admin.
- Writes only to `%APPDATA%\RCN1Bridge` (settings, profiles) and `%LOCALAPPDATA%\RCN1Bridge` (logs), plus the `HKCU\...\Run` value when the user turns on "Start with Windows". Turning it off removes the value.
- No keyboard or mouse hooks, no injecting into other processes, no reading other processes' memory.
- Never downloads or runs anything. "Install driver" opens the official ViGEmBus release page in the browser. "Check for updates" reads the GitHub releases API and links to the page; it never replaces its own exe.
- No telemetry. The only network call is the update check, and it can be turned off.

**Release checklist**
1. Upload both zips to VirusTotal before publishing. Link the results in the release notes.
2. Any detection: submit a false-positive report to that vendor and to Microsoft (Defender submission portal) before publishing. Don't rename or repack to dodge it.
3. Keep the same build pipeline between releases. Reputation builds up per signing identity and per file.

## Testing

Core unit tests:
- CRC-8 and CRC-16 against frames v1 builds (sim-enable and poll with known sequence numbers).
- Packet builder round-trips through the parser.
- Parser: split reads at every byte boundary, garbage between frames, bad CRCs, length fields 0 to 1023, a fuzz run of a million random bytes. It must never throw and must never emit a frame with a bad CRC.
- Stick decoders for both frame sizes.
- Calibration, deadzone, expo and rate with table-driven cases, including asymmetric calibration.
- Mapper: profile JSON to report, stick modes, button thresholds.
- Zero-allocation check on the decode-to-report path.

Replay: a saved Inspector capture can be fed through the whole Core pipeline in a test, so hardware bugs become regression tests.

Manual hardware checklist (per release): cold start with RC on, RC powered on after launch, unplug while live (sticks centre within 250 ms), power off while plugged in, replug into another port, sleep/resume, two hours of continuous use with no CPU or memory growth, and the Cyberpunk FPV mod end to end.

## Milestones

Status as of 4 Oct 2026.

1. **Core.** Done. Protocol, parser, CRCs, decoders, input processing, with tests.
2. **Live path.** Done. Device engine, port discovery, ViGEm output, Home page. Replaces v1.
3. **Inspector and button hunt.** Done. Diagnostics page with message list and button capture; buttons and the C/N/S switch decoded from 06 / 27 and shown on Home. Buttons aren't sent to the game yet.
4. **Mapping and tuning.** Next. Profiles, mapping page (including button bindings and the C/N/S pulses), tuning page, calibration wizard.
5. **Shell.** Mostly done: setup checklist, Settings page, tray, start with Windows, theme. Still to do: a newer build taking over the running copy, connect/disconnect notifications.
6. **Release.** GitHub Actions build, both zips, attestations, VirusTotal, SignPath application, Nexus page update. The C# app now lives at the repo root; the Python v1 is on the `legacy` branch.
7. **Mod side.** The Drone mod changes from [Cyberpunk FPV mod integration](#cyberpunk-fpv-mod-integration). Unblocked now that the switch is decoded.

## Decisions

- Code signing: SignPath Foundation (free).
- Python v1: on the `legacy` branch.
- C/N/S switch: drives the FPV mod's flight mode and profile.

## Open questions

- Whether Codeware key callbacks see pad buttons (blocks milestone 7's approach).
- How to fix the dial-on-B conflict in Standard mode.

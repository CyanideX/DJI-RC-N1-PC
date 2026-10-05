# Game link

RC-N1 Bridge publishes the remote's state to a block of shared memory, so a game mod can read the RC directly instead of through a virtual Xbox controller. Anything running as the same Windows user can read it.

For Cyberpunk 2077 there's a ready-made reader: the **RCN1Link** RED4ext plugin, which gives redscript and CET mods a single `RCN1.Read()` call. Drone uses it. The rest of this page is for writing a reader of your own; for C or C++ there is one ready to copy.

The bridge publishes live data when **Settings > Send the controller to** is "Both" or "Mods only". With "Virtual Xbox controller only" the block is still there, with the off flag set.

## Opening it

- Name: `Local\RCN1Bridge.GameLink.2`
- Size: 256 bytes, little-endian
- The bridge creates it at startup. If it doesn't exist, the bridge isn't running: try again every couple of seconds.
- Open it read/write. A reader only ever writes its own slot (see [Reader slots](#reader-slots)).

## Versions

Bytes 4 and 6 hold a major and a minor version. Within a major, fields are only ever added in reserved space, and the minor goes up. Reject a major you don't know, and accept any minor at or above the one you were written for. A new major also gets a new block name, so an old reader just sees no bridge instead of reading the wrong offsets.

Version 1 (bridge 4.1, `Local\RCN1Bridge.GameLink`, 128 bytes) is retired. Nothing that shipped read it.

## Layout (version 2.0)

| Offset | Type | Field |
|---|---|---|
| 0 | u32 | Magic `0x314E4352` ("RCN1") |
| 4 | u16 | Major version, 2 |
| 6 | u16 | Minor version, 0 |
| 8 | u32 | Size of the block, 256 |
| 12 | u32 | Bridge process ID. 0 once the bridge has quit |
| 16 | i64 | Bridge heartbeat, in `QueryPerformanceCounter` ticks. Written 4 times a second while the bridge runs, RC or not. 0 once it has quit |
| 24 | u32 | Device: 1 RC-N1 |
| 28 | | Reserved |
| **32** | **u32** | **Sequence. Odd while the bridge is writing. Everything from here to 128 is covered by it** |
| 36 | u32 | Flags, see below |
| 40 | i64 | Time of the last write, in `QueryPerformanceCounter` ticks |
| 48 | u32 | Sample count. Goes up by one on every write, so you can skip data you've already seen |
| 52 | u8 | Flight mode switch: 0 unknown, 1 C, 2 N, 3 S |
| 53 | | Reserved |
| 54 | u16 | Buttons held: bit 0 Fn, bit 1 Photo/Video, bit 2 RTH, bit 3 Capture |
| 56 | f32 x6 | Tuned axes: left X, left Y, right X, right Y, gimbal dial, reserved. -1 to 1, after the bridge's calibration, deadzone, expo, rate and smoothing |
| 80 | f32 x6 | Calibrated axes, same order. Calibration and deadzone only. Use these if your game applies its own rates |
| 104 | u8 x8 | Press counters, one per bit of the held buttons. Each goes up by one on every press and wraps at 256 |
| 112 | u16 x5 | Raw left H, left V, right H, right V, dial. About 364 to 1684, centre 1024 |
| 122 | | Reserved |
| 124 | u16 | Raw button word from the RC (message 06/27) |
| 126 | | Reserved |
| 128 | 32 B x4 | Reader slots |

Flags:

| Bit | Meaning |
|---|---|
| 0 | Live: the RC is connected and the user is sending to mods |
| 1 | The buttons, switch and button word are valid |
| 2 | The dial is valid |
| 3 | Paused: the user turned "Send to game" off |
| 4 | Off: the user picked "Virtual Xbox controller only" |

The sticks follow DJI Mode 2: the left stick is throttle (Y) and yaw (X), the right stick is pitch (Y) and roll (X). Up and right are positive.

## Reading

The bridge writes about 175 times a second from one thread, using a sequence lock:

1. Read the sequence at 32. If it's odd, the bridge is mid-write: read it again.
2. Copy bytes 36 to 128.
3. Read the sequence again. If it changed, go back to step 1.

Then decide what the data is worth:

- **Bridge running**: the process ID is not 0 and the bridge heartbeat is under 2 seconds old.
- **Live**: the live flag is set and the write time is under 250 ms old. Otherwise treat the sticks as centred, the same as the bridge does. If the paused or off flag is set, you can tell the player why.

`QueryPerformanceCounter` is one clock for the whole machine, so you can compare it with the timestamps directly.

## Button presses

Don't compare held bits frame to frame: a quick tap can start and end between two of your frames. Keep your own copy of the press counters instead. A counter that moved since your last read is a press, however short it was. Only act on presses while the data is live, and refresh your copy whenever it isn't.

## Reader slots

The four slots at 128 let the bridge show who is reading, and let a reader ask for the RC to itself. Each slot is 32 bytes:

| Offset | Type | Field |
|---|---|---|
| 0 | u32 | Process ID of the reader. 0 means free |
| 4 | u32 | Flags: bit 0 exclusive |
| 8 | i64 | Heartbeat, `QueryPerformanceCounter` |
| 16 | char x16 | Name shown in the bridge, UTF-8, zero-padded. Name the game, for example "Cyberpunk 2077" |

To claim one, find a slot whose process ID is 0 or whose heartbeat is more than 2 seconds old, and swap your process ID in with `InterlockedCompareExchange`. If the swap fails, try the next slot. Then write the name and flags, and write the heartbeat on every read. Check now and then that the slot still holds your process ID, and claim again if it doesn't.

The bridge counts a slot only while its heartbeat is under 2 seconds old. A slot keeps its place when the bridge restarts, so a game that is already running carries on.

**Exclusive**: in "Both", the bridge holds its virtual Xbox controller at centre while any reader with the exclusive flag is attached, so the game doesn't get the sticks twice. Set it when your mod uses the RC, and clear it, or stop reading, when it doesn't.

## Reader for C and C++

[`sdk/rcn1link.h`](../sdk/rcn1link.h) is a complete reader in one header: opening, version check, sequence lock, slot claiming, heartbeat and the live check. Copy it into your mod.

```c
#include "rcn1link.h"

rcn1_link link;
rcn1_init(&link, "My Game");
rcn1_set_exclusive(&link, 1);   // optional

// every frame
rcn1_reading r;
rcn1_read(&link, &r);
if (r.live)
    Steer(r.state.calibrated[0], r.state.calibrated[1]);

// on unload
rcn1_close(&link);
```

The header is the bridge's own definition of the layout: a test fails if it ever disagrees with the writer. Readers in other languages can follow its `#define`s offset by offset.

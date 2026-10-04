# Game link

RC-N1 Bridge publishes the remote's state to a block of shared memory, so a game mod can read the RC directly instead of through a virtual Xbox controller. The Drone mod for Cyberpunk 2077 reads it with a RED4ext plugin. Anything else running as the same Windows user can read it too.

The bridge publishes only when **Settings > Send the controller to** is "Drone mod only" or "Both". With "Virtual Xbox controller" the block still exists but the live flag stays clear.

## Opening it

- Name: `Local\RCN1Bridge.GameLink`
- Size: 128 bytes, little-endian
- The bridge creates it at startup. If it doesn't exist, the bridge isn't running: try again every couple of seconds.
- Open it read/write. The only field a reader writes is the heartbeat.

## Layout (version 1)

| Offset | Type | Field |
|---|---|---|
| 0 | u32 | Magic `0x314E4352` ("RCN1") |
| 4 | u32 | Layout version, currently 1. Reject anything else |
| 8 | u32 | Sequence. Odd while the bridge is writing |
| 12 | u32 | Flags: bit 0 live, bit 1 buttons valid, bit 2 dial valid |
| 16 | i64 | Time of the last write, in `QueryPerformanceCounter` ticks |
| 24 | f32 x5 | Left X, left Y, right X, right Y, gimbal dial. -1 to 1, after the bridge's calibration and tuning |
| 44 | u16 | Raw button word from the RC (message 06/27) |
| 46 | u8 | Flight mode switch: 0 unknown, 1 C, 2 N, 3 S |
| 47 | u8 | Buttons held: bit 0 Fn, bit 1 Photo/Video, bit 2 RTH, bit 3 Capture |
| 48 | u16 x5 | Raw left H, left V, right H, right V, dial. About 364 to 1684, centre 1024 |
| 58 | | Reserved |
| 64 | i64 | Reader heartbeat: write `QueryPerformanceCounter` here on every read. The bridge shows "Drone mod connected" while it's under 2 seconds old |
| 72 | | Reserved to 128 |

The sticks follow DJI Mode 2: left stick is throttle (Y) and yaw (X), right stick is pitch (Y) and roll (X). Up and right are positive.

## Reading

The bridge writes about 175 times a second from one thread, using a sequence lock:

1. Read the sequence. If it's odd, the bridge is mid-write: read it again.
2. Copy bytes 12 to 58.
3. Read the sequence again. If it changed, go back to step 1.

Then check freshness. `QueryPerformanceCounter` is one clock for the whole machine, so compare it with the timestamp: anything older than 250 ms means the RC stopped answering, so treat the sticks as centred, the same as the bridge does. Treat the data as live only when the live flag is set and the timestamp is fresh.

```cpp
struct GameLinkState { uint32_t flags; int64_t time; float axes[5]; uint16_t word; uint8_t mode, buttons; uint16_t raw[5]; };

bool Read(const volatile uint8_t* block, GameLinkState& out)
{
    for (int tries = 0; tries < 8; tries++) {
        uint32_t before = *(const volatile uint32_t*)(block + 8);
        if (before & 1) continue;
        std::atomic_thread_fence(std::memory_order_acquire);
        memcpy(&out, (const void*)(block + 12), sizeof(out));
        std::atomic_thread_fence(std::memory_order_acquire);
        if (*(const volatile uint32_t*)(block + 8) == before) return true;
    }
    return false;
}
```

`GameLinkState` above matches bytes 12 to 58 only when packed (`#pragma pack(push, 1)`).

// rcn1link.h: reader for the RC-N1 Bridge game link, layout 2. Copy this one file into your mod.
// Windows, C or C++. Layout and the rules behind it: docs/GameLink.md
//
//   rcn1_link link;
//   rcn1_init(&link, "My Game");          // name shown in the bridge, up to 15 bytes of UTF-8
//   rcn1_set_exclusive(&link, 1);         // optional: bridge centres its virtual pad while you read
//
//   rcn1_reading r;                        // every frame
//   rcn1_read(&link, &r);
//   if (r.live) Steer(r.state.calibrated[0], r.state.calibrated[1]);
//
//   rcn1_close(&link);                    // on unload; frees the slot at once
//
// Not thread-safe: one rcn1_link per thread, or lock around it.
#pragma once

#include <Windows.h>
#include <stdint.h>
#include <string.h>

#define RCN1_BLOCK_NAME L"Local\\RCN1Bridge.GameLink.2"
#define RCN1_SIZE 256
#define RCN1_MAGIC 0x314E4352
#define RCN1_MAJOR 2
#define RCN1_MINOR 0
#define RCN1_DEVICE_RC_N1 1

#define RCN1_MAJOR_OFFSET 4
#define RCN1_MINOR_OFFSET 6
#define RCN1_SIZE_OFFSET 8
#define RCN1_WRITER_PID_OFFSET 12
#define RCN1_WRITER_BEAT_OFFSET 16
#define RCN1_DEVICE_OFFSET 24
#define RCN1_SEQUENCE_OFFSET 32
#define RCN1_FLAGS_OFFSET 36
#define RCN1_TIMESTAMP_OFFSET 40
#define RCN1_SAMPLE_OFFSET 48
#define RCN1_SWITCH_OFFSET 52
#define RCN1_HELD_OFFSET 54
#define RCN1_AXES_OFFSET 56
#define RCN1_CALIBRATED_OFFSET 80
#define RCN1_PRESS_OFFSET 104
#define RCN1_RAW_OFFSET 112
#define RCN1_BUTTON_WORD_OFFSET 124
#define RCN1_SLOTS_OFFSET 128

#define RCN1_SLOT_SIZE 32
#define RCN1_SLOT_COUNT 4
#define RCN1_SLOT_PID_OFFSET 0
#define RCN1_SLOT_FLAGS_OFFSET 4
#define RCN1_SLOT_BEAT_OFFSET 8
#define RCN1_SLOT_NAME_OFFSET 16
#define RCN1_SLOT_NAME_LENGTH 16

#define RCN1_FLAG_LIVE 1
#define RCN1_FLAG_BUTTONS 2
#define RCN1_FLAG_DIAL 4
#define RCN1_FLAG_PAUSED 8
#define RCN1_FLAG_OFF 16

#define RCN1_BUTTON_FN 1
#define RCN1_BUTTON_PHOTO_VIDEO 2
#define RCN1_BUTTON_RETURN_HOME 4
#define RCN1_BUTTON_CAPTURE 8

#define RCN1_SLOT_EXCLUSIVE 1

// Matches the bridge's own stall watchdog
#define RCN1_STALE_MS 250
#define RCN1_ALIVE_MS 2000
// Opening is a kernel call; no point hammering it every frame while the bridge is closed
#define RCN1_RETRY_MS 2000

#pragma pack(push, 1)
// Bytes 36 to 128 of the block, everything the sequence lock covers
typedef struct rcn1_state
{
    uint32_t flags;
    int64_t time;
    uint32_t sample;
    uint8_t switch_pos;
    uint8_t reserved0;
    uint16_t held;
    float axes[6];
    float calibrated[6];
    uint8_t presses[8];
    uint16_t raw[5];
    uint16_t reserved1;
    uint16_t button_word;
    uint16_t reserved2;
} rcn1_state;
#pragma pack(pop)

#ifdef __cplusplus
static_assert(sizeof(rcn1_state) == RCN1_SLOTS_OFFSET - RCN1_FLAGS_OFFSET, "rcn1_state layout");
#else
_Static_assert(sizeof(rcn1_state) == RCN1_SLOTS_OFFSET - RCN1_FLAGS_OFFSET, "rcn1_state layout");
#endif

typedef struct rcn1_reading
{
    rcn1_state state;
    // Bridge process running, RC or not
    int bridge;
    // Live flag set and data under 250 ms old. While 0, axes and held read 0; presses stay put.
    int live;
    // The block is there but from a bridge with another major version
    int version_mismatch;
} rcn1_reading;

typedef struct rcn1_link
{
    HANDLE mapping;
    volatile uint8_t* block;
    char name[RCN1_SLOT_NAME_LENGTH];
    int exclusive;
    int slot; // -1 for none
    ULONGLONG next_try;
    int64_t stale_ticks;
    int64_t alive_ticks;
    rcn1_state last;
} rcn1_link;

#define RCN1__AT(link, type, offset) (*(volatile type*)((link)->block + (offset)))
#define RCN1__SLOT(index) (RCN1_SLOTS_OFFSET + (index) * RCN1_SLOT_SIZE)

static inline void rcn1_init(rcn1_link* link, const char* name)
{
    size_t length = strlen(name);
    memset(link, 0, sizeof(*link));
    link->slot = -1;
    // One byte short so the name always ends in 0
    memcpy(link->name, name, length < RCN1_SLOT_NAME_LENGTH ? length : RCN1_SLOT_NAME_LENGTH - 1);
}

static inline void rcn1__write_slot_flags(rcn1_link* link)
{
    if (link->block && link->slot >= 0)
        RCN1__AT(link, uint32_t, RCN1__SLOT(link->slot) + RCN1_SLOT_FLAGS_OFFSET) = link->exclusive ? RCN1_SLOT_EXCLUSIVE : 0;
}

static inline void rcn1_set_exclusive(rcn1_link* link, int exclusive)
{
    link->exclusive = exclusive != 0;
    rcn1__write_slot_flags(link);
}

static inline void rcn1__release_slot(rcn1_link* link)
{
    if (link->block && link->slot >= 0)
    {
        int slot = RCN1__SLOT(link->slot);
        if (RCN1__AT(link, LONG, slot + RCN1_SLOT_PID_OFFSET) == (LONG)GetCurrentProcessId())
        {
            RCN1__AT(link, int64_t, slot + RCN1_SLOT_BEAT_OFFSET) = 0;
            RCN1__AT(link, LONG, slot + RCN1_SLOT_PID_OFFSET) = 0;
        }
    }
    link->slot = -1;
}

static inline void rcn1__unmap(rcn1_link* link)
{
    if (link->block)
        UnmapViewOfFile((LPCVOID)link->block);
    if (link->mapping)
        CloseHandle(link->mapping);
    link->block = NULL;
    link->mapping = NULL;
    link->slot = -1;
}

static inline void rcn1_close(rcn1_link* link)
{
    rcn1__release_slot(link);
    rcn1__unmap(link);
}

// 1 matches, 0 still being written by a starting bridge, -1 another major version
static inline int rcn1__header(rcn1_link* link)
{
    uint32_t magic = RCN1__AT(link, uint32_t, 0);
    if (magic == 0)
        return 0;
    return magic == RCN1_MAGIC && RCN1__AT(link, uint16_t, RCN1_MAJOR_OFFSET) == RCN1_MAJOR ? 1 : -1;
}

static inline int rcn1__open(rcn1_link* link, rcn1_reading* out)
{
    ULONGLONG now = GetTickCount64();
    LARGE_INTEGER frequency;
    if (now < link->next_try)
        return 0;
    link->next_try = now + RCN1_RETRY_MS;

    link->mapping = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, RCN1_BLOCK_NAME);
    if (!link->mapping)
        return 0;
    link->block = (volatile uint8_t*)MapViewOfFile(link->mapping, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, RCN1_SIZE);
    if (!link->block || rcn1__header(link) != 1)
    {
        out->version_mismatch = link->block && rcn1__header(link) < 0;
        rcn1__unmap(link);
        return 0;
    }

    QueryPerformanceFrequency(&frequency);
    link->stale_ticks = frequency.QuadPart * RCN1_STALE_MS / 1000;
    link->alive_ticks = frequency.QuadPart * RCN1_ALIVE_MS / 1000;
    return 1;
}

// The slot can go missing under us: another reader took it after a hitch longer than the timeout
static inline void rcn1__claim(rcn1_link* link, int64_t now)
{
    LONG pid = (LONG)GetCurrentProcessId();
    int i;
    if (link->slot >= 0 && RCN1__AT(link, LONG, RCN1__SLOT(link->slot) + RCN1_SLOT_PID_OFFSET) == pid)
        return;

    link->slot = -1;
    for (i = 0; i < RCN1_SLOT_COUNT; i++)
    {
        int slot = RCN1__SLOT(i);
        volatile LONG* owner = &RCN1__AT(link, LONG, slot + RCN1_SLOT_PID_OFFSET);
        LONG current = *owner;
        int open = current == 0 || current == pid || now - RCN1__AT(link, int64_t, slot + RCN1_SLOT_BEAT_OFFSET) > link->alive_ticks;
        if (!open || InterlockedCompareExchange(owner, pid, current) != current)
            continue;

        link->slot = i;
        RCN1__AT(link, int64_t, slot + RCN1_SLOT_BEAT_OFFSET) = now;
        memcpy((void*)(link->block + slot + RCN1_SLOT_NAME_OFFSET), link->name, RCN1_SLOT_NAME_LENGTH);
        rcn1__write_slot_flags(link);
        return;
    }
}

static inline int rcn1__read_state(rcn1_link* link, rcn1_state* out)
{
    volatile uint32_t* sequence = &RCN1__AT(link, uint32_t, RCN1_SEQUENCE_OFFSET);
    int tries;
    for (tries = 0; tries < 16; tries++)
    {
        uint32_t before = *sequence;
        if (before & 1)
        {
            YieldProcessor();
            continue;
        }
        MemoryBarrier();
        memcpy(out, (const void*)(link->block + RCN1_FLAGS_OFFSET), sizeof(*out));
        MemoryBarrier();
        if (*sequence == before)
            return 1;
    }
    return 0;
}

// Opens the block when the bridge appears, keeps the slot and heartbeat, and copies the state.
// One seqlock read, cheap enough for every frame.
static inline void rcn1_read(rcn1_link* link, rcn1_reading* out)
{
    LARGE_INTEGER now;
    rcn1_state state;
    int64_t writer_beat;
    memset(out, 0, sizeof(*out));
    if (!link->block && !rcn1__open(link, out))
        return;

    // A bridge of another version can take over the block while we hold it open
    switch (rcn1__header(link))
    {
    case 0:
        return;
    case -1:
        out->version_mismatch = 1;
        rcn1_close(link);
        return;
    }

    QueryPerformanceCounter(&now);
    rcn1__claim(link, now.QuadPart);
    if (link->slot >= 0)
        RCN1__AT(link, int64_t, RCN1__SLOT(link->slot) + RCN1_SLOT_BEAT_OFFSET) = now.QuadPart;

    if (rcn1__read_state(link, &state))
        link->last = state;

    writer_beat = RCN1__AT(link, int64_t, RCN1_WRITER_BEAT_OFFSET);
    out->bridge = RCN1__AT(link, uint32_t, RCN1_WRITER_PID_OFFSET) != 0 && writer_beat != 0
        && now.QuadPart - writer_beat < link->alive_ticks;
    out->live = out->bridge && (link->last.flags & RCN1_FLAG_LIVE) && now.QuadPart - link->last.time < link->stale_ticks;
    out->state = link->last;
    if (!out->live)
    {
        // Centred, so anything still reading axes after a stall sees hands-off
        memset(out->state.axes, 0, sizeof(out->state.axes));
        memset(out->state.calibrated, 0, sizeof(out->state.calibrated));
        out->state.held = 0;
    }
}

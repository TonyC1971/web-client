/*
 * SDL3.c — registers "SDL3" as a PInvoke module + hand-written
 * forwarders for the SDL3 functions ClassicUO actually reaches.
 *
 * Paired with tools/patch-sdl3-legacy.py which emits
 * sdl3_stubs.c (1028 weak empty stubs). Strong symbols defined
 * here override the weak auto-stubs at link time.
 *
 * Module-name registration trick: the WebAssembly SDK's PInvoke
 * table generator (WasmApp.Common.targets:748 _WasmPInvokeModules)
 * builds its module list from NativeFileReference basenames. This
 * file's basename is `SDL3`, so `[DllImport("SDL3", EntryPoint =
 * "sdl3_Foo")]` can resolve — entry points now live in a
 * collision-free `sdl3_*` symbol namespace.
 *
 * P4d.7 addition: a DOM -> managed input bridge that bypasses
 * SDL's own event pump. The previous attempt to wire CUO's
 * SDL_EventFilter through reverse-PInvoke from SDL2's queue
 * crashed the Mono interpreter at interp.c:488 on the first
 * event (see tools/test-bot-memory.md "P4d.7 attempt"). Instead:
 *
 *   JS  -> Module.ccall("wasm_push_*")           (push into g_event_buf)
 *   C#  -> sdl3_drain_events(byte*, int)          (drain to caller buffer)
 *   C#  -> for each event: GameController.HandleSdlEvent(&ev)
 *
 * No pinvoke from C# during event ingestion; only during drain.
 * No reverse-PInvoke at all. No SDL_AddEventWatch / SetEventFilter.
 * The Mono interp never handles a callback whose signature it
 * doesn't know at pinvoke-table-gen time.
 */

#include <SDL.h>
#include <emscripten.h>
#include <stdint.h>
#include <string.h>

/* KEEPALIVE marker so the file's translation unit isn't dead-
 * stripped before the "SDL3" basename lands in the module list. */
EMSCRIPTEN_KEEPALIVE
int p4d_sdl3_marker(void) {
    return (int)(long)(void*)SDL_GetError;
}

/* ---------- SDL3 event-type constants (lifted from
 * SDL3.Legacy.cs so we don't need an SDL3 header, which the
 * Emscripten SDL2 port doesn't ship) ---------------------- */
#define SDL3_EVENT_WINDOW_MOUSE_ENTER  524
#define SDL3_EVENT_WINDOW_MOUSE_LEAVE  525
#define SDL3_EVENT_WINDOW_FOCUS_GAINED 526
#define SDL3_EVENT_WINDOW_FOCUS_LOST   527
#define SDL3_EVENT_KEY_DOWN            768
#define SDL3_EVENT_KEY_UP              769
#define SDL3_EVENT_TEXT_INPUT          771
#define SDL3_EVENT_MOUSE_MOTION        1024
#define SDL3_EVENT_MOUSE_BUTTON_DOWN   1025
#define SDL3_EVENT_MOUSE_BUTTON_UP     1026
#define SDL3_EVENT_MOUSE_WHEEL         1027

/* ---------- Event ring buffer ---------- */
/*
 * Each slot is one SDL_Event (128 bytes per SDL3.Legacy.cs).
 * Byte offsets below match SDL3.Legacy.cs LayoutKind.Sequential
 * on wasm32 (pointers = 4 bytes). A divergence here = silent
 * mis-dispatched event in HandleSdlEvent.
 *
 * Common header (all event types):
 *   [ 0..4 )   uint32  type
 *   [ 4..8 )   uint32  reserved
 *   [ 8..16)   uint64  timestamp
 *
 * Keyboard (SDL3_EVENT_KEY_{DOWN,UP}):
 *   [16..20)   uint32  windowID
 *   [20..24)   uint32  which
 *   [24..28)   uint32  scancode
 *   [28..32)   uint32  key            (SDL3 keycode)
 *   [32..34)   uint16  mod            (SDL_Keymod)
 *   [34..36)   uint16  raw
 *   [36..37)   uint8   down
 *   [37..38)   uint8   repeat
 *
 * Text input (SDL3_EVENT_TEXT_INPUT):
 *   [16..20)   uint32  windowID
 *   [20..24)   ptr     text           (wasm32 pointer)
 *   The UTF-8 payload is stashed at offset WASM_TEXT_OFFSET inside
 *   the *caller's* SDL_Event copy, and the pointer is patched to
 *   point into that copy during drain. See sdl3_drain_events().
 *
 * Mouse motion (SDL3_EVENT_MOUSE_MOTION):
 *   [16..20)   uint32  windowID
 *   [20..24)   uint32  which
 *   [24..28)   uint32  state (button mask)
 *   [28..32)   float   x
 *   [32..36)   float   y
 *   [36..40)   float   xrel
 *   [40..44)   float   yrel
 *
 * Mouse button (SDL3_EVENT_MOUSE_BUTTON_{DOWN,UP}):
 *   [16..20)   uint32  windowID
 *   [20..24)   uint32  which
 *   [24..25)   uint8   button
 *   [25..26)   uint8   down
 *   [26..27)   uint8   clicks
 *   [27..28)   uint8   padding
 *   [28..32)   float   x
 *   [32..36)   float   y
 *
 * Mouse wheel (SDL3_EVENT_MOUSE_WHEEL):
 *   [16..20)   uint32  windowID
 *   [20..24)   uint32  which
 *   [24..28)   float   x
 *   [28..32)   float   y
 *   [32..36)   uint32  direction
 *   [36..40)   float   mouse_x
 *   [40..44)   float   mouse_y
 */
#define WASM_EVENT_SLOT_SIZE 128
#define WASM_EVENT_MAX       256              /* must be power of two */
#define WASM_TEXT_OFFSET      64              /* inside event slot */
#define WASM_TEXT_MAX         60              /* 60 bytes + NUL padding */

static uint8_t  g_event_buf[WASM_EVENT_MAX][WASM_EVENT_SLOT_SIZE];
static uint32_t g_event_head = 0;             /* next write */
static uint32_t g_event_tail = 0;             /* next read */
static uint64_t g_dropped_events = 0;

/* Cached polled mouse state (updated on motion + button events).
 * CUO's Mouse.Update() reaches SDL_GetMouseState every tick; we
 * answer from this cache instead of the browser DOM directly so
 * the value is stable within a single frame. */
static float    g_mouse_x = 0.0f;
static float    g_mouse_y = 0.0f;
static uint32_t g_mouse_mask = 0;             /* SDL_MouseButtonFlags */

static inline void wasm_write_u32(uint8_t* p, uint32_t v) {
    memcpy(p, &v, 4);
}
static inline void wasm_write_u16(uint8_t* p, uint16_t v) {
    memcpy(p, &v, 2);
}
static inline void wasm_write_f32(uint8_t* p, float v) {
    memcpy(p, &v, 4);
}

/* Reserve the next write slot. Returns NULL if the ring is full
 * (drop the event — inputs coming in faster than C# can drain
 * them means the browser is contending with a frame hitch; a
 * dropped click is better than a stalled tab). */
static uint8_t* reserve_slot(void) {
    uint32_t next = (g_event_head + 1u) & (WASM_EVENT_MAX - 1u);
    if (next == g_event_tail) {
        g_dropped_events++;
        return NULL;
    }
    uint8_t* slot = &g_event_buf[g_event_head][0];
    memset(slot, 0, WASM_EVENT_SLOT_SIZE);
    g_event_head = next;
    return slot;
}

/* ---------- Push helpers (called from main.js via Module.ccall) ---------- */

/* down=1 for keydown, 0 for keyup. keycode/scancode/mod match
 * SDL3 semantics (browser -> SDL3 mapping happens JS-side). */
EMSCRIPTEN_KEEPALIVE
void wasm_push_key(int32_t down, uint32_t keycode, uint32_t scancode,
                   uint32_t mod, int32_t repeat) {
    uint8_t* s = reserve_slot();
    if (!s) return;
    wasm_write_u32(s +  0, down ? SDL3_EVENT_KEY_DOWN : SDL3_EVENT_KEY_UP);
    wasm_write_u32(s + 16, 1u);                       /* windowID */
    wasm_write_u32(s + 24, scancode);
    wasm_write_u32(s + 28, keycode);
    wasm_write_u16(s + 32, (uint16_t)mod);
    s[36] = (uint8_t)(down ? 1 : 0);
    s[37] = (uint8_t)(repeat ? 1 : 0);
}

/* JS hands us a NUL-terminated UTF-8 C string (Emscripten's ccall
 * 'string' marshalling allocates the buffer + writes it). We cap
 * at WASM_TEXT_MAX-1 bytes + NUL. strlen is fine here — the
 * 'string' coercion guarantees a terminator. */
EMSCRIPTEN_KEEPALIVE
void wasm_push_text(const char* utf8_cstr) {
    if (utf8_cstr == NULL) return;
    size_t len = strlen(utf8_cstr);
    if (len == 0) return;
    if (len > WASM_TEXT_MAX - 1) len = WASM_TEXT_MAX - 1;
    uint8_t* s = reserve_slot();
    if (!s) return;
    wasm_write_u32(s +  0, SDL3_EVENT_TEXT_INPUT);
    wasm_write_u32(s + 16, 1u);                       /* windowID */
    /* text.text pointer (offset 20) is left zero in the ring; it
     * gets patched to point inside the caller's drain slot during
     * sdl3_drain_events. We store the bytes inline at
     * WASM_TEXT_OFFSET now, and the drain copies them with the
     * rest of the 128-byte slot. */
    memcpy(s + WASM_TEXT_OFFSET, utf8_cstr, len);
    s[WASM_TEXT_OFFSET + len] = 0;                    /* NUL-terminate */
}

EMSCRIPTEN_KEEPALIVE
void wasm_push_mouse_motion(float x, float y, float xrel, float yrel,
                            uint32_t buttons) {
    g_mouse_x = x;
    g_mouse_y = y;
    g_mouse_mask = buttons;

    uint8_t* s = reserve_slot();
    if (!s) return;
    wasm_write_u32(s +  0, SDL3_EVENT_MOUSE_MOTION);
    wasm_write_u32(s + 16, 1u);                       /* windowID */
    wasm_write_u32(s + 24, buttons);                  /* state */
    wasm_write_f32(s + 28, x);
    wasm_write_f32(s + 32, y);
    wasm_write_f32(s + 36, xrel);
    wasm_write_f32(s + 40, yrel);
}

/* button is SDL's 1-based mouse button number (1=left, 2=middle,
 * 3=right, 4=x1, 5=x2). down=1 for press, 0 for release. */
EMSCRIPTEN_KEEPALIVE
void wasm_push_mouse_button(int32_t down, int32_t button, float x, float y) {
    /* Keep mask in sync so SDL_GetMouseState sees the new bit. */
    uint32_t bit = 0;
    switch (button) {
        case 1: bit = 0x01; break;  /* LMASK */
        case 2: bit = 0x02; break;  /* MMASK */
        case 3: bit = 0x04; break;  /* RMASK */
        case 4: bit = 0x08; break;  /* X1MASK */
        case 5: bit = 0x10; break;  /* X2MASK */
    }
    if (down) g_mouse_mask |=  bit;
    else      g_mouse_mask &= ~bit;
    g_mouse_x = x;
    g_mouse_y = y;

    uint8_t* s = reserve_slot();
    if (!s) return;
    wasm_write_u32(s +  0,
        down ? SDL3_EVENT_MOUSE_BUTTON_DOWN : SDL3_EVENT_MOUSE_BUTTON_UP);
    wasm_write_u32(s + 16, 1u);                       /* windowID */
    s[24] = (uint8_t)button;
    s[25] = (uint8_t)(down ? 1 : 0);
    s[26] = 1;                                        /* clicks */
    s[27] = 0;                                        /* padding */
    wasm_write_f32(s + 28, x);
    wasm_write_f32(s + 32, y);
}

EMSCRIPTEN_KEEPALIVE
void wasm_push_mouse_wheel(float dx, float dy, float mouse_x, float mouse_y) {
    uint8_t* s = reserve_slot();
    if (!s) return;
    wasm_write_u32(s +  0, SDL3_EVENT_MOUSE_WHEEL);
    wasm_write_u32(s + 16, 1u);                       /* windowID */
    wasm_write_f32(s + 24, dx);
    wasm_write_f32(s + 28, dy);
    wasm_write_u32(s + 32, 0u);                       /* direction=NORMAL */
    wasm_write_f32(s + 36, mouse_x);
    wasm_write_f32(s + 40, mouse_y);
}

/* 1 = mouse entered the canvas, 0 = left it. CUO reads this to
 * toggle Mouse.MouseInWindow. */
EMSCRIPTEN_KEEPALIVE
void wasm_push_mouse_in_window(int32_t in_window) {
    uint8_t* s = reserve_slot();
    if (!s) return;
    wasm_write_u32(s +  0,
        in_window ? SDL3_EVENT_WINDOW_MOUSE_ENTER
                  : SDL3_EVENT_WINDOW_MOUSE_LEAVE);
    wasm_write_u32(s + 16, 1u);                       /* windowID */
}

EMSCRIPTEN_KEEPALIVE
void wasm_push_window_focus(int32_t focused) {
    uint8_t* s = reserve_slot();
    if (!s) return;
    wasm_write_u32(s +  0,
        focused ? SDL3_EVENT_WINDOW_FOCUS_GAINED
                : SDL3_EVENT_WINDOW_FOCUS_LOST);
    wasm_write_u32(s + 16, 1u);                       /* windowID */
}

/* ---------- Managed drain entry point ---------- */

/* Copies up to `max` pending events from the ring into `dst`, each
 * event occupying exactly WASM_EVENT_SLOT_SIZE (128) bytes. For
 * TEXT_INPUT events, the text pointer inside the event struct is
 * patched to point into the dst buffer itself (the UTF-8 payload
 * is memcpy'd alongside the rest of the slot), so the managed
 * caller can follow sdlEvent->text.text as a plain byte* without
 * further marshalling. Returns the number of events written. */
EMSCRIPTEN_KEEPALIVE
int32_t sdl3_drain_events(uint8_t* dst, int32_t max) {
    if (dst == NULL || max <= 0) return 0;
    int32_t count = 0;
    while (g_event_tail != g_event_head && count < max) {
        uint8_t* src     = &g_event_buf[g_event_tail][0];
        uint8_t* dst_evt = dst + (size_t)count * WASM_EVENT_SLOT_SIZE;
        memcpy(dst_evt, src, WASM_EVENT_SLOT_SIZE);

        uint32_t type;
        memcpy(&type, dst_evt, 4);
        if (type == SDL3_EVENT_TEXT_INPUT) {
            /* Patch text.text (offset 20) to point inside the
             * caller's own copy so the pointer outlives the ring
             * slot (which may be reused the moment the caller
             * returns). */
            uint32_t p = (uint32_t)(uintptr_t)(dst_evt + WASM_TEXT_OFFSET);
            wasm_write_u32(dst_evt + 20, p);
        }

        g_event_tail = (g_event_tail + 1u) & (WASM_EVENT_MAX - 1u);
        count++;
    }
    return count;
}

/* Diagnostic getter — count of events dropped because the ring
 * was full. Bot can poll this to catch regressions. */
EMSCRIPTEN_KEEPALIVE
uint64_t sdl3_dropped_events(void) {
    return g_dropped_events;
}

/* ---------- Persistence bridge ---------- */

/* Triggered from C# via [DllImport("SDL3", EntryPoint="wasm_flush_idbfs")]
 * on the auto-save cadence inside GameController.Update. Calls the JS
 * function main.js installed at globalThis.__wasm_flush_idbfs, which
 * runs Module.FS.syncfs(false, ...) to persist /Data (CUO profile,
 * macros, gump positions) from MEMFS into IndexedDB. The call is
 * fire-and-forget from C#'s perspective — the IDB transaction runs
 * async and commits on its own. EM_ASM is safe here because we are on
 * Mono's main thread (called straight from Update), matching the
 * thread that owns the JS VM. */
EMSCRIPTEN_KEEPALIVE
void wasm_flush_idbfs(void) {
    EM_ASM({
        if (typeof globalThis.__wasm_flush_idbfs === 'function') {
            try { globalThis.__wasm_flush_idbfs(); }
            catch (e) { console.warn('[wasm_flush_idbfs] threw:', e); }
        }
    });
}

/* ---------- Real SDL3 forwarders (strong symbols override the
 *            weak auto-stubs in sdl3_stubs.c) ------------------ */

/* HiDPI scale of the window. SDL3 returns a float like 1.0, 1.5,
 * 2.0. In the browser, CUO consumes this as `DpiScale` which
 * multiplies the logical window size — returning 0 (the default
 * weak stub) collapses the window to 0x0 and downstream SDL3
 * window-resize calls pass garbage sizes. 1.0f is a safe default
 * on wasm; real browser DPR integration lands in a later
 * milestone. */
EMSCRIPTEN_KEEPALIVE
float sdl3_SDL_GetWindowDisplayScale(void* window) {
    (void)window;
    return 1.0f;
}

/* Mouse state queries — CUO's Mouse.Update() calls one of these
 * each frame. The weak stubs return 0 without writing to the out
 * pointers, so Mouse.Position stayed at (0, 0) regardless of the
 * actual cursor location. Now we return the cached values
 * maintained by wasm_push_mouse_motion / wasm_push_mouse_button. */
EMSCRIPTEN_KEEPALIVE
uint32_t sdl3_SDL_GetMouseState(float* x, float* y) {
    if (x) *x = g_mouse_x;
    if (y) *y = g_mouse_y;
    return g_mouse_mask;
}

EMSCRIPTEN_KEEPALIVE
uint32_t sdl3_SDL_GetGlobalMouseState(float* x, float* y) {
    /* Browser has no concept of global vs window — the canvas IS
     * the window. Serve the same cached coords; CUO subtracts the
     * window origin (0,0 on wasm) before using them. */
    if (x) *x = g_mouse_x;
    if (y) *y = g_mouse_y;
    return g_mouse_mask;
}

/* Window position is (0, 0) because the canvas IS the window on
 * wasm. CUO's Mouse.Update() calls this when MouseInWindow is
 * false to translate global -> window-local; returning zero keeps
 * those two coord systems identical. */
EMSCRIPTEN_KEEPALIVE
uint32_t sdl3_SDL_GetWindowPosition(void* window, int32_t* x, int32_t* y) {
    (void)window;
    if (x) *x = 0;
    if (y) *y = 0;
    return 1;   /* SDLBool TRUE */
}

/* ---------- Audio bridge ----------------------------------------
 *
 * FAudio is not linked on wasm (signature-mismatch against SDL2.a's
 * bundled SDL3 — the last publish warned about SDL_GetTicks). Rather
 * than wrestle with a native audio stack in the browser, route CUO
 * audio directly through the Web Audio API: main.js installs
 * `globalThis.__wasm_play_pcm / __wasm_play_music / __wasm_stop_sound
 * / __wasm_set_sound_volume` and these EM_ASM shims forward the raw
 * PCM / MP3 bytes. UOSound plays raw 16-bit mono PCM; UOMusic plays
 * MP3 via Web Audio's `decodeAudioData` + `loop`.
 *
 * Handles returned by play_pcm / play_music are opaque sound IDs the
 * JS side uses to locate the AudioBufferSourceNode for stop / volume
 * updates. 0 means failure or audio context unavailable.
 * ---------------------------------------------------------------- */

/* IMPORTANT: audio shims use MAIN_THREAD_EM_ASM_INT, not EM_ASM_INT.
 * Reason: the .NET 10 multithread runtime runs CUO game code on a
 * deputy worker. EM_ASM_INT executes in the CALLING thread's JS
 * context — on deputy, `globalThis` is the WorkerGlobalScope, which
 * does NOT have the `__wasm_play_*` handlers installed. Those are
 * installed by main.js's `wireWasmAudio()` IIFE on main thread only.
 * Before this fix, every audio call from deputy returned handle 0
 * and JS never saw the play request (no `[audio] play_music_url`
 * log). MAIN_THREAD_EM_ASM_INT proxies the JS to main thread where
 * the handlers live. Cost: sync RPC round-trip per call — acceptable
 * for audio (~100/min during gameplay). Reported 2026-04-24 by user
 * after the 0376273/8b502ebc4 "audio-works" pair turned out to be a
 * transient false-positive. */

EMSCRIPTEN_KEEPALIVE
int32_t wasm_play_pcm(const uint8_t* data, int32_t len,
                      float volume, int32_t sample_rate,
                      int32_t channels, int32_t loop) {
    return MAIN_THREAD_EM_ASM_INT({
        if (typeof globalThis.__wasm_play_pcm === 'function') {
            try { return globalThis.__wasm_play_pcm($0, $1, $2, $3, $4, $5); }
            catch (e) { console.warn('[wasm_play_pcm] threw:', e); }
        }
        return 0;
    }, (intptr_t)data, len, volume, sample_rate, channels, loop);
}

EMSCRIPTEN_KEEPALIVE
int32_t wasm_play_music(const uint8_t* data, int32_t len,
                        float volume, int32_t loop) {
    return MAIN_THREAD_EM_ASM_INT({
        if (typeof globalThis.__wasm_play_music === 'function') {
            try { return globalThis.__wasm_play_music($0, $1, $2, $3); }
            catch (e) { console.warn('[wasm_play_music] threw:', e); }
        }
        return 0;
    }, (intptr_t)data, len, volume, loop);
}

EMSCRIPTEN_KEEPALIVE
void wasm_stop_sound(int32_t handle) {
    MAIN_THREAD_EM_ASM({
        if (typeof globalThis.__wasm_stop_sound === 'function') {
            try { globalThis.__wasm_stop_sound($0); }
            catch (e) { console.warn('[wasm_stop_sound] threw:', e); }
        }
    }, handle);
}

EMSCRIPTEN_KEEPALIVE
void wasm_set_sound_volume(int32_t handle, float volume) {
    MAIN_THREAD_EM_ASM({
        if (typeof globalThis.__wasm_set_sound_volume === 'function') {
            try { globalThis.__wasm_set_sound_volume($0, $1); }
            catch (e) { /* volume updates are fire-and-forget */ }
        }
    }, handle, volume);
}

/* Fire-and-forget C# -> JS lifecycle signal. Replaces the
 * Console.Error.WriteLine + main.js stderr/stdout sniff pattern that
 * the JS side previously used to detect "[cuo-trace] LoginGump
 * added", "[cuo-trace] LoginSteps=EnteringBritania",
 * "[cuo-trace] Draw#60", and "[zonediag] ... GameScene active". The
 * sniff was fragile (relied on console.* never being silenced or
 * buffered) and leaked the trace strings into prod DevTools. With
 * this bridge, C# calls wasm_signal_event("login-gump-added") and
 * main.js's __uo_signal handler dispatches a CustomEvent
 * `cuo:login-gump-added` that JS listeners pick up directly.
 *
 * MAIN_THREAD_EM_ASM is required: the deputy worker (where C# runs)
 * has its own globalThis, but __uo_signal is installed on the main
 * thread by main.js. Same pattern as wasm_play_pcm / wasm_stop_sound
 * — see the comment block at line ~414 for the full rationale.
 *
 * Caller responsibility: pass a stable, lowercase, hyphen-separated
 * event name (the suffix after `cuo:` in the dispatched event). The
 * names are part of the public main.js contract — renaming requires
 * updating both ends. */
EMSCRIPTEN_KEEPALIVE
void wasm_signal_event(const char *name) {
    MAIN_THREAD_EM_ASM({
        if (typeof globalThis.__uo_signal === 'function') {
            try { globalThis.__uo_signal(UTF8ToString($0)); }
            catch (e) { /* listeners are best-effort */ }
        }
    }, name);
}

/* Async sibling of wasm_signal_event for TUO (TazUO).
 *
 * The blocking MAIN_THREAD_EM_ASM version above deadlocked TUO's deputy
 * worker when emitted from the login/world-entry path: the deputy spins
 * (futex-waits) for the main thread to service the proxied call, but
 * TUO's main thread is not pumping the SYNC proxy queue at that moment.
 * (TUO disables audio on wasm, so it had never exercised ANY blocking
 * MAIN_THREAD_EM_ASM shim before — see memory note
 * feedback_tuo_no_blocking_mainthread_rpc + the iter61 outage.)
 *
 * MAIN_THREAD_ASYNC_EM_ASM posts the call to the main thread's ASYNC
 * queue and returns IMMEDIATELY — the deputy never blocks, so a deadlock
 * is structurally impossible. Worst case (main never drains the async
 * queue) is a dropped signal, never a hang. CUO keeps using the blocking
 * wasm_signal_event above; only TUO binds this entrypoint.
 *
 * String lifetime: the blocking variant keeps the deputy parked until the
 * main thread reads `name`, so the P/Invoke-marshalled pointer stays
 * valid. Async returns before the block runs, so the marshalled buffer is
 * already freed by then — we strdup onto the shared heap and the queued
 * block frees the copy. malloc/free are thread-safe under emscripten
 * pthreads + shared memory, so freeing on main a pointer allocated on the
 * deputy is fine (one shared allocator). The free is guarded so an
 * unexported _free degrades to a negligible (~17 byte) leak, not an error. */
EMSCRIPTEN_KEEPALIVE
void wasm_signal_event_async(const char *name) {
    if (!name) return;
    char *copy = strdup(name);
    if (!copy) return;
    MAIN_THREAD_ASYNC_EM_ASM({
        try {
            if (typeof globalThis.__uo_signal === 'function') {
                globalThis.__uo_signal(UTF8ToString($0));
            }
        } catch (e) { /* listeners are best-effort */ }
        try { _free($0); } catch (e) { /* tiny leak if _free unavailable */ }
    }, copy);
}

/* ---------- Clipboard bridge ------------------------------------
 *
 * CUO text boxes (StbTextBox) drive Ctrl+C / Ctrl+X / Ctrl+V through
 * SDL2's SDL_SetClipboardText / SDL_GetClipboardText. On wasm those
 * resolve into SDL2.a's bundled clipboard — an in-process buffer with
 * no connection to the browser / OS clipboard, so copy never reached
 * other apps and paste never saw text copied outside the game. CUO
 * C# (ClassicUO.Utility/WasmClipboard.cs) routes through these shims
 * instead under BROWSER_WASM.
 *
 * main.js wireWasmClipboard() installs:
 *   __wasm_clipboard_set(text) -> mirror + navigator.clipboard.writeText
 *   __wasm_clipboard_get()     -> returns the mirror string
 * and keeps the mirror fed from the DOM `paste` event (the only
 * synchronous, permission-free read of the real system clipboard).
 *
 * MAIN_THREAD_EM_ASM is mandatory — same deputy-worker rationale as
 * the audio block above: the __wasm_clipboard_* handlers are
 * installed on the main thread only. */
EMSCRIPTEN_KEEPALIVE
void wasm_clipboard_set(const uint8_t* utf8, int32_t len) {
    MAIN_THREAD_EM_ASM({
        if (typeof globalThis.__wasm_clipboard_set === 'function') {
            try { globalThis.__wasm_clipboard_set(UTF8ToString($0, $1)); }
            catch (e) { console.warn('[wasm_clipboard_set] threw:', e); }
        }
    }, utf8, len);
}

/* Copies the JS-side clipboard mirror as UTF-8 into `buf` (capacity
 * `max` bytes) and returns the number of bytes written, clamped to
 * `max`. The result is NOT null-terminated — C# slices [0, len).
 * Mirrors sdl3_drain_events' caller-buffer fill convention. */
EMSCRIPTEN_KEEPALIVE
int32_t wasm_clipboard_get(uint8_t* buf, int32_t max) {
    return MAIN_THREAD_EM_ASM_INT({
        if (typeof globalThis.__wasm_clipboard_get !== 'function') return 0;
        var s;
        try { s = globalThis.__wasm_clipboard_get(); }
        catch (e) { console.warn('[wasm_clipboard_get] threw:', e); return 0; }
        if (typeof s !== 'string' || s.length === 0) return 0;
        var enc = new TextEncoder().encode(s);
        var n = enc.length < $1 ? enc.length : $1;
        HEAPU8.set(enc.subarray(0, n), $0);
        return n;
    }, (intptr_t)buf, max);
}

/* Play music via a URL fetch instead of an in-memory byte buffer.
 * UOMusic's MP3 files are too large to pre-mount into MEMFS
 * (100+ tracks totalling ~200 MB), and the REQUIRED_FILES list in
 * main.js only covers .mul game assets. This shim accepts a
 * null-terminated URL string pointing at the music served via the
 * `gamefiles/` junction (e.g. "gamefiles/Music/Digital/britain1.mp3")
 * and hands it to JS which fetch()es + decodeAudioData()s it. */
EMSCRIPTEN_KEEPALIVE
int32_t wasm_play_music_url(const char* url, float volume, int32_t loop) {
    return MAIN_THREAD_EM_ASM_INT({
        if (typeof globalThis.__wasm_play_music_url === 'function') {
            try { return globalThis.__wasm_play_music_url(UTF8ToString($0), $1, $2); }
            catch (e) { console.warn('[wasm_play_music_url] threw:', e); }
        }
        return 0;
    }, url, volume, loop);
}

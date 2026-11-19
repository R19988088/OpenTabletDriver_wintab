//! Wintab32 implementation backed by a shared-memory ring buffer written by the
//! OpenTabletDriver output mode. Applications load this DLL and receive
//! synthetic tablet packets without any kernel driver.

use std::{
    ffi::c_void,
    ptr,
    sync::{
        atomic::{AtomicPtr, AtomicU32, Ordering},
        Mutex, OnceLock,
    },
};
use windows_sys::Win32::{
    Foundation::{HANDLE, HINSTANCE, HWND},
    System::{
        Memory::{
            MapViewOfFile, OpenFileMappingW, FILE_MAP_ALL_ACCESS,
        },
        SystemInformation::GetTickCount,
        SystemServices::DLL_PROCESS_ATTACH,
    },
    UI::Input::KeyboardAndMouse::{
        mouse_event, MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP, MOUSEEVENTF_MIDDLEDOWN,
        MOUSEEVENTF_MIDDLEUP, MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP,
    },
    UI::WindowsAndMessaging::{IsWindow, PostMessageW},
};

// ---------------------------------------------------------------- shared memory

const SHARED_NAME: &str = "Local\\OpenTabletDriverWintab";
const SHARED_MAGIC: u32 = 0x4F54_5754; // "OTWT"
const RING_CAPACITY: usize = 512;
const MAX_X: i32 = 44800;
const MAX_Y: i32 = 29600;
/// Raw pressure range carried by the OpenTabletDriver shared buffer.
const RAW_PRESSURE_MAX: u32 = 32767;
/// Normal pressure range. The installed Wacom 6.3.38 driver reports
/// DVC_NPRESSURE.axMax = 32767 (confirmed by dumping its WTInfo surface), not
/// 1023. Advertising 1023 made SAI treat the packet value as a tiny fraction
/// of the range; match the official driver instead.
const MAX_PRESSURE: u32 = 32767;
/// Tangent pressure keeps the much smaller official range (1023).
const MAX_TANGENT_PRESSURE: u32 = 1023;
/// 100 lines per inch in FIX32 — what Wacom's driver reports for X and Y.
const RES_100_LPI: u32 = 100 << 16;
const PKT_RATE: u32 = 200;
/// Packet history kept for serial lookups. A hardware driver queues a handful
/// of packets, so a consumer that falls behind sees input from a few
/// milliseconds ago at most. Keeping 256 (1.3 s at 200 Hz) meant an application
/// that briefly stopped polling - switching windows is enough - came back to a
/// backlog of ancient samples and replayed a whole stroke's worth of stale
/// positions at once, which Photoshop shows as a single dot.
const MAX_QUEUE: usize = 16;

/// Diagnostics go to a fixed, per-process file instead of the process's current
/// directory. Living next to the DLL only worked when the app happened to start
/// in a writable folder; keeping the log beside the loading executable is what
/// lets us tell "Photoshop never loaded the shim" apart from "it loaded and read
/// the wrong bytes".
static DIAG_PATH: std::sync::OnceLock<Option<std::path::PathBuf>> = std::sync::OnceLock::new();
static DIAG_HEADER: std::sync::OnceLock<()> = std::sync::OnceLock::new();

fn diag_file() -> Option<&'static std::path::PathBuf> {
    DIAG_PATH
        .get_or_init(|| {
            let dir = std::env::var_os("LOCALAPPDATA")
                .map(std::path::PathBuf::from)
                .unwrap_or_else(std::env::temp_dir)
                .join("OTDWintab");
            std::fs::create_dir_all(&dir).ok()?;
            let exe = std::env::current_exe().ok()?;
            let stem = exe
                .file_stem()
                .map(|s| s.to_string_lossy().to_string())
                .unwrap_or_else(|| "process".to_string());
            Some(dir.join(format!("wintab-{}-{}.log", stem, std::process::id())))
        })
        .as_ref()
}

/// Runtime experiment switches, refreshed once a second, so behaviour can be
/// A/B tested by creating or deleting a file instead of rebuilding the DLL.
///   silent.txt     - stop emitting packets entirely (tells whether an
///                    application draws from our packets or from the injected
///                    system mouse)
///   proxstatus.txt - report TPS_PROXIMITY while the pen is in range
static FORCE_PROX_STATUS: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);

fn experiment(name: &str) -> bool {
    use std::sync::atomic::{AtomicU64, Ordering};
    use std::time::{SystemTime, UNIX_EPOCH};
    static STAMP: AtomicU64 = AtomicU64::new(0);
    static SILENT: AtomicU64 = AtomicU64::new(0);
    static PROX: AtomicU64 = AtomicU64::new(0);
    static SPREAD: AtomicU64 = AtomicU64::new(0);

    let now = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_millis() as u64)
        .unwrap_or(0);
    if now.saturating_sub(STAMP.load(Ordering::Relaxed)) > 1000 {
        STAMP.store(now, Ordering::Relaxed);
        let dir = std::env::var_os("LOCALAPPDATA")
            .map(std::path::PathBuf::from)
            .unwrap_or_else(std::env::temp_dir)
            .join("OTDWintab");
        SILENT.store(u64::from(dir.join("silent.txt").exists()), Ordering::Relaxed);
        let prox = dir.join("proxstatus.txt").exists();
        PROX.store(u64::from(prox), Ordering::Relaxed);
        FORCE_PROX_STATUS.store(prox, Ordering::Relaxed);
        SPREAD.store(
            u64::from(dir.join("spreadpressure.txt").exists()),
            Ordering::Relaxed,
        );
    }
    match name {
        "silent" => SILENT.load(Ordering::Relaxed) != 0,
        "proxstatus" => PROX.load(Ordering::Relaxed) != 0,
        other => other == "spreadpressure" && SPREAD.load(Ordering::Relaxed) != 0,
    }
}

fn diag(msg: &str) {
    let Some(path) = diag_file() else { return };
    DIAG_HEADER.get_or_init(|| {
        let exe = std::env::current_exe()
            .map(|p| p.display().to_string())
            .unwrap_or_default();
        let bits = if cfg!(target_pointer_width = "64") { "x64" } else { "x86" };
        let _ = std::fs::write(
            path,
            format!("{} [{}] pid={}\n", exe, bits, std::process::id()),
        );
    });
    use std::io::Write;
    if let Ok(mut f) = std::fs::OpenOptions::new().append(true).create(true).open(path) {
        let _ = writeln!(f, "{:>8} {}", unsafe { GetTickCount() }, msg);
    }
}

/// API entry trace. Logs the first calls of each export in a process, so an
/// application that loads the shim and then never pulls a packet shows up as a
/// missing call rather than as an absence of evidence.
fn trace(name: &str) {
    static CALLS: AtomicU32 = AtomicU32::new(0);
    let seen = CALLS.fetch_add(1, Ordering::Relaxed);
    if seen < 200 {
        diag(&format!("CALL {}", name));
    }
}

#[repr(C)]
#[derive(Clone, Copy)]
struct SharedPacket {
    sequence: u32,
    flags: u32,
    x: i32,
    y: i32,
    pressure: u32,
    tilt_x: i32,
    tilt_y: i32,
    rotation: i32,
    buttons: u32,
    cursor: u32,
    time_ms: u32,
    in_range: u32,
}

#[repr(C)]
struct SharedState {
    magic: u32,
    version: u32,
    enabled: u32,
    heartbeat_ms: u32,
    width: i32,
    height: i32,
    max_pressure: u32,
    write_index: AtomicU32,
    packets: [SharedPacket; RING_CAPACITY],
}

static VIEW: AtomicPtr<SharedState> = AtomicPtr::new(ptr::null_mut());



fn shared() -> Option<&'static SharedState> {
    let cached = VIEW.load(Ordering::Acquire);
    if !cached.is_null() {
        return Some(unsafe { &*cached });
    }

    let mut name: Vec<u16> = SHARED_NAME.encode_utf16().collect();
    name.push(0);
    let handle: HANDLE = unsafe { OpenFileMappingW(FILE_MAP_ALL_ACCESS, 0, name.as_ptr()) };
    if handle.is_null() {
        return None;
    }

    let view = unsafe {
        MapViewOfFile(handle, FILE_MAP_ALL_ACCESS, 0, 0, std::mem::size_of::<SharedState>())
    };
    let state = view.Value as *mut SharedState;
    if state.is_null() || unsafe { (*state).magic } != SHARED_MAGIC {
        return None;
    }

    VIEW.store(state, Ordering::Release);
    Some(unsafe { &*state })
}

// ---------------------------------------------------------------- pen state

#[derive(Clone, Copy)]
struct PenState {
    x: i32,
    y: i32,
    pressure: u32,
    tilt_x: i32,
    tilt_y: i32,
    rotation: i32,
    buttons: u32,
    /// Held-button bitmask, latched from Wintab transition codes. Absolute-mode
    /// consumers test bit 0 for the tip, so this must stay set for the whole
    /// stroke instead of reflecting the one-shot TBN_DOWN sample.
    latched_buttons: u32,
    /// Set once a sample carries a TBN_DOWN/TBN_UP transition, which tells us the
    /// publisher sends event codes rather than a plain held-state mask.
    transition_buttons: bool,
    in_range: bool,
    /// Wintab pkSerialNumber, incremented once per emitted packet.
    serial: u32,
    /// Shared-ring read cursor. Must not be the same counter as `serial`: the
    /// ring wraps every RING_CAPACITY samples, so reusing it made packet
    /// serials jump and desynchronised packet lookup from the queue.
    ring_cursor: u32,
    changed: u32,
    last_packet: Option<SharedPacket>,
    last_reported_in_range: bool,
}

impl PenState {
    /// pkButtons as the context's `lcPktMode` asks for it.
    ///
    /// Absolute mode (bit clear) reports the held buttons as a bitmask. Relative
    /// mode reports a one-shot transition: low word = button number, high word =
    /// TBN_DOWN/TBN_UP. Wacom's samples set `PACKETMODE = PK_BUTTONS` and test
    /// `HIWORD(pkt.pkButtons) == TBN_DOWN`, so the event form must be produced
    /// for them; apps that leave the bit clear test bit 0 for the tip instead.
    /// Latch a published button value into the held-state mask.
    ///
    /// The older C# OTD plugin in userdata publishes Wintab transition codes
    /// (HIWORD = TBN_DOWN/TBN_UP, LOWORD = button index); newer plugins publish
    /// the held-state bitmask directly. Absolute-mode consumers test bit 0 for
    /// the tip, so transitions must be accumulated: forwarding the one-shot
    /// TBN_DOWN value made every following packet look like a lifted pen and
    /// applications reported zero pressure for the rest of the stroke.
    fn latch_buttons(&mut self, raw: u32, in_range: bool) {
        self.buttons = raw;
        if !in_range {
            self.latched_buttons = 0;
            return;
        }
        let high = raw >> 16;
        let low = raw & 0xffff;
        if high != 0 {
            // First transition seen: this plugin publishes event codes, so a
            // later zero means "held, nothing new" rather than "no buttons".
            self.transition_buttons = true;
            match high {
                2 => self.latched_buttons |= 1u32 << (low & 31), // TBN_DOWN
                1 => self.latched_buttons &= !(1u32 << (low & 31)), // TBN_UP
                _ => {}
            }
            return;
        }
        if self.transition_buttons {
            return;
        }
        self.latched_buttons = raw;
    }

    fn buttons_for_mode(&self, packet_mode: u32) -> u32 {
        if packet_mode & PK_BUTTONS == 0 {
            // The older C# Wintab output plugin in userdata publishes the
            // transition form (HIWORD = TBN_DOWN/TBN_UP, LOWORD = button
            // index). Absolute-mode consumers test bit 0 for the tip, so the
            // latched mask is what they must see — forwarding the one-shot
            // 0x00020000 made every later packet look like a lifted pen, and
            // applications then reported zero pressure.
            self.latched_buttons
        } else {
            // Relative mode keeps the event form: LOWORD button, HIWORD transition.
            self.buttons
        }
    }
}

impl Default for PenState {
    fn default() -> Self {
        Self {
            x: 0,
            y: 0,
            pressure: 0,
            tilt_x: 0,
            tilt_y: 0,
            rotation: 0,
            buttons: 0,
            latched_buttons: 0,
            transition_buttons: false,
            in_range: false,
            serial: 0,
            ring_cursor: 0,
            changed: 0,
            last_packet: None,
            last_reported_in_range: false,
        }
    }
}

/// Pull the newest sample out of the shared ring. Returns true when the pen
/// state changed in a way worth emitting as a packet.
fn poll_shared(pen: &mut PenState) -> bool {
    let Some(state) = shared() else {
        let was_in_range = pen.in_range;
        pen.in_range = false;
        pen.pressure = 0;
        pen.changed = PK_STATUS;
        return was_in_range;
    };

    let now = unsafe { GetTickCount() };
    let heartbeat = unsafe { ptr::read_volatile(&state.heartbeat_ms) };
    let alive = now.wrapping_sub(heartbeat) < 2500;
    let enabled = unsafe { ptr::read_volatile(&state.enabled) } != 0;

    if !alive || !enabled {
        let was_in_range = pen.in_range;
        pen.in_range = false;
        pen.pressure = 0;
        pen.changed = PK_STATUS;
        return was_in_range;
    }

    let index = state.write_index.load(Ordering::Acquire);
    let mut cursor = pen.ring_cursor;
    if index == cursor {
        return false;
    }
    if index.wrapping_sub(cursor) > RING_CAPACITY as u32 {
        cursor = index.wrapping_sub(RING_CAPACITY as u32);
    }

    let mut latest = None;
    while cursor != index {
        // Slot holds the sample whose sequence is cursor + 1.
        let slot = (cursor as usize) % RING_CAPACITY;
        latest = Some(unsafe { ptr::read_volatile(&state.packets[slot]) });
        cursor = cursor.wrapping_add(1);
    }

    let Some(packet) = latest else {
        return false;
    };

    let previous = pen.last_packet;
    pen.last_packet = Some(packet);
    pen.ring_cursor = index;
    pen.serial = next_serial(pen.serial, packet.sequence);
    pen.x = packet.x;
    pen.y = packet.y;
    pen.pressure = packet.pressure.min(RAW_PRESSURE_MAX);
    pen.tilt_x = packet.tilt_x;
    pen.tilt_y = packet.tilt_y;
    pen.rotation = packet.rotation;
    pen.buttons = packet.buttons;
    pen.in_range = packet.in_range != 0;

    let mut changed = PK_X | PK_Y | PK_NORMAL_PRESSURE | PK_ORIENTATION | PK_ROTATION;
    if previous.map_or(true, |p| p.buttons != packet.buttons) {
        changed |= PK_BUTTONS;
    }
    if previous.map_or(true, |p| p.cursor != packet.cursor) {
        changed |= PK_CURSOR;
    }
    if previous.map_or(true, |p| p.in_range != packet.in_range) {
        changed |= PK_STATUS;
    }
    pen.changed = changed;
    true
}

// ---------------------------------------------------------------- constants

const PK_CONTEXT: u32 = 0x0001;
const PK_STATUS: u32 = 0x0002;
const PK_TIME: u32 = 0x0004;
const PK_CHANGED: u32 = 0x0008;
const PK_SERIAL_NUMBER: u32 = 0x0010;
const PK_CURSOR: u32 = 0x0020;
const PK_BUTTONS: u32 = 0x0040;
const PK_X: u32 = 0x0080;
const PK_Y: u32 = 0x0100;
const PK_Z: u32 = 0x0200;
const PK_NORMAL_PRESSURE: u32 = 0x0400;
const PK_TANGENT_PRESSURE: u32 = 0x0800;
const PK_ORIENTATION: u32 = 0x1000;
const PK_ROTATION: u32 = 0x2000;

const TPS_NONE: u32 = 0;

const CXO_SYSTEM: u32 = 0x0001;
const CXO_PEN: u32 = 0x0002;
const CXO_MESSAGES: u32 = 0x0004;

// Wacom's official driver exposes cursor 0 as a Puck (CSR_TYPE 0x8000) and
// cursor 1 as "Pressure Stylus" with CSR_TYPE 0x4842. The 0x4842 value is
// 0x0802 (generic stylus) plus Wacom's pressure-stylus bits; Photoshop checks
// those bits before it accepts pkNormalPressure. Reporting 0x8000 here made
// Photoshop classify the pen as a puck/mouse, which is why the brush panel
// stayed flat and tip clicks were mapped wrong.
const CSRPEN: u32 = 0x4842;

const WTI_INTERFACE: u32 = 1;
const WTI_STATUS: u32 = 2;
const WTI_DEFCONTEXT: u32 = 3;
const WTI_DEFSYSCTX: u32 = 4;
const WTI_DEVICES: u32 = 100;
const WTI_CURSORS: u32 = 200;
const WTI_DDCTXS: u32 = 400;
const WTI_DSCTXS: u32 = 500;

const IFC_WINTABID: u32 = 1;
const IFC_SPECVERSION: u32 = 2;
const IFC_IMPLVERSION: u32 = 3;
const IFC_NDEVICES: u32 = 4;
const IFC_NCURSORS: u32 = 5;
const IFC_NCONTEXTS: u32 = 6;
const IFC_CTXOPTIONS: u32 = 7;
const IFC_CTXSAVESIZE: u32 = 8;
const IFC_NEXTENSIONS: u32 = 9;
const IFC_NMANAGERS: u32 = 10;

const DVC_NAME: u32 = 1;
const DVC_HARDWARE: u32 = 2;
const DVC_NCSRTYPES: u32 = 3;
const DVC_FIRSTCSR: u32 = 4;
const DVC_PKTRATE: u32 = 5;
const DVC_PKTDATA: u32 = 6;
const DVC_PKTMODE: u32 = 7;
const DVC_CSRDATA: u32 = 8;
const DVC_XMARGIN: u32 = 9;
const DVC_YMARGIN: u32 = 10;
const DVC_ZMARGIN: u32 = 11;
const DVC_X: u32 = 12;
const DVC_Y: u32 = 13;
const DVC_Z: u32 = 14;
const DVC_NPRESSURE: u32 = 15;
const DVC_TPRESSURE: u32 = 16;
const DVC_ORIENTATION: u32 = 17;
const DVC_ROTATION: u32 = 18;
const DVC_PNPID: u32 = 19;

const CSR_NAME: u32 = 1;
const CSR_ACTIVE: u32 = 2;
const CSR_PKTDATA: u32 = 3;
const CSR_BUTTONS: u32 = 4;
const CSR_NPBUTTON: u32 = 9;
const CSR_TYPE: u32 = 20;

// The rest of the cursor declaration. An application asks for these before it
// decides whether a cursor can report pressure at all; answering zero to a
// required item is what makes it treat the device as pressureless.
const CSR_BUTTONBITS: u32 = 5;
const CSR_BTNNAMES: u32 = 6;
const CSR_BUTTONMAP: u32 = 7;
const CSR_SYSBTNMAP: u32 = 8;
const CSR_NPBTNMARKS: u32 = 10;
const CSR_NPRESPONSE: u32 = 11;
const CSR_TPBUTTON: u32 = 12;
const CSR_TPBTNMARKS: u32 = 13;
const CSR_TPRESPONSE: u32 = 14;
const CSR_PHYSID: u32 = 15;
const CSR_MODE: u32 = 16;
const CSR_MINPKTDATA: u32 = 17;
const CSR_MINBUTTONS: u32 = 18;
const CSR_CAPABILITIES: u32 = 19;

const STA_CONTEXTS: u32 = 1;
const STA_SYSCTXS: u32 = 2;
const STA_PKTRATE: u32 = 3;
const STA_PKTDATA: u32 = 4;
const STA_MANAGERS: u32 = 5;
const STA_SYSTEM: u32 = 6;
const STA_BUTTONUSE: u32 = 7;
const STA_SYSBTNUSE: u32 = 8;

const DVC_MAX: u32 = 19;
const CSR_MAX: u32 = 20;
const STA_MAX: u32 = 8;

const TU_NONE: u32 = 0;
const TU_CM: u32 = 2;
const TU_CIRCLE: u32 = 3;

/// Stamped into every diagnostic log so a log can be tied to the exact binary
/// that produced it. If a log lacks this marker, the process is still running
/// an older Wintab32.dll.
const SHIM_BUILD: &str = "20261114-z80";

/// How long a context may go without a consume call before the notifier treats
/// the application as "away" and stops mirroring every sample into WT_PACKET.
/// A foreground application answers within a few milliseconds, so a window
/// switched to the background is what crosses this line.
const CONSUMER_IDLE_MS: u32 = 1200;

/// Posting interval used while the application is away. It has to stay well
/// above the point where messages pile up, but small enough that an application
/// coming back is told about the newest packet almost immediately.
const CONSUMER_AWAY_INTERVAL_MS: u32 = 200;

/// How long the pen may produce no new sample before the driver declares it out
/// of proximity. The digitiser only interrupts when something changes, so a pen
/// resting still or lifted away can leave the last sample saying "in range"
/// indefinitely. Without this timeout the application is never told the tool
/// left, keeps its stroke machinery armed, and treats the next touch as a
/// continuation: no motion, one dot at the pressure it last saw.
const PROXIMITY_TIMEOUT_MS: u32 = 1200;

/// Silence tolerated while the tip is pressed. A held-still stroke reports
/// nothing either, and cutting proximity there would break the stroke, so only
/// a much longer silence means the tool really left the tablet.
const PROXIMITY_TIMEOUT_PRESSED_MS: u32 = 3000;

// ---------------------------------------------------------------- contexts

#[repr(C)]
#[derive(Clone, Copy)]
pub struct LogContextA {
    name: [u8; 40],
    options: u32,
    status: u32,
    locks: u32,
    msg_base: u32,
    device: u32,
    pkt_rate: u32,
    pkt_data: u32,
    pkt_mode: u32,
    move_mask: u32,
    btn_dn_mask: u32,
    btn_up_mask: u32,
    in_org: [i32; 3],
    in_ext: [i32; 3],
    out_org: [i32; 3],
    out_ext: [i32; 3],
    sens: [u32; 3],
    sys_mode: i32,
    sys_org: [i32; 2],
    sys_ext: [i32; 2],
    sys_sens: [u32; 2],
}

#[repr(C)]
#[derive(Clone, Copy)]
pub struct LogContextW {
    name: [u16; 40],
    options: u32,
    status: u32,
    locks: u32,
    msg_base: u32,
    device: u32,
    pkt_rate: u32,
    pkt_data: u32,
    pkt_mode: u32,
    move_mask: u32,
    btn_dn_mask: u32,
    btn_up_mask: u32,
    in_org: [i32; 3],
    in_ext: [i32; 3],
    out_org: [i32; 3],
    out_ext: [i32; 3],
    sens: [u32; 3],
    sys_mode: i32,
    sys_org: [i32; 2],
    sys_ext: [i32; 2],
    sys_sens: [u32; 2],
}

#[repr(C)]
struct Axis {
    min: i32,
    max: i32,
    units: u32,
    resolution: u32,
}

struct Context {
    packet_data: u32,
    packet_mode: u32,
    /// Options the application asked for; `CXO_SYSTEM` means this context owns
    /// the system cursor and must inject real mouse buttons.
    options: u32,
    /// The lcSysExt this application believes in. Applications read it once,
    /// from WTI_DDCTXS at startup, and never refresh it; after a monitor is
    /// unplugged SAI keeps mapping packets through the old, wider rectangle and
    /// paints outside the screen. Storing the value makes it possible to undo
    /// that stale scaling at serialization time.
    sys_ext: [i32; 2],
    sys_org: [i32; 2],
    pen: PenState,
    queue: Vec<QueuedPacket>,
    read: usize,
    enabled: bool,
    /// Window that receives WT_PACKET notifications, and the base id for them.
    hwnd: usize,
    msg_base: u32,
    /// Serial of the newest packet already announced by WT_PACKET.
    last_notified_serial: u32,
    /// Last proximity state announced to the application.
    proximity_sent: bool,
    /// Set when the pen enters range or its cursor index changes. The notifier
    /// turns it into a WT_CSRCHANGE message: older applications wait for that
    /// announcement before trusting the cursor's pressure capability.
    csr_change_pending: bool,
    csr_cursor: u32,
    /// Highest pressure seen since the current diagnostic window opened.
    /// The per-packet dump samples one serial per second, which lands on a
    /// hovering pen often enough to look like "pressure is always zero".
    peak_pressure: u32,
    peak_samples: u32,
    /// Highest pkNormalPressure actually written into a serialized packet in
    /// the current window. `peak_pressure` only proves the sample had pressure;
    /// this proves the bytes the application reads carry it.
    peak_packed_pressure: u32,
    peak_window_start: u32,
    /// Button mask last injected into the system mouse, for CXO_SYSTEM contexts.
    system_buttons: u32,
    /// One-pole state for the feel panel's stroke stabilisation.
    feel_smoothed: f32,
    /// WTPacketsGet calls since the diagnostic window opened. Distinguishes
    /// "the shim stopped producing" from "the application stopped polling".
    get_calls: u32,
    /// Serial of the newest packet produced, kept after the application drains
    /// the queue, so a fast poller is still told that new input arrived.
    produced_serial: u32,
    /// Tick of the application's most recent consume call (WTPacket /
    /// WTPacketsGet). A window that is switched away stops consuming while its
    /// message queue keeps accepting notifications, so this is what the
    /// notifier uses to tell "idle pen" from "absent application".
    last_consume_ms: u32,
    /// Tick of the most recent WT_PACKET posted to this context.
    last_notify_ms: u32,
    /// WT_PACKET posts held back because the application stopped consuming.
    suppressed_posts: u32,
    /// Tick of the newest packet built from a fresh sample, used by the
    /// proximity timeout in refresh().
    last_sample_ms: u32,
    /// Worst case in the current diagnostic window: how old the delivered
    /// sample was (ms, measured against OTD's own timestamp) and how many
    /// samples sat unconsumed in the shared ring when we went to read.
    peak_age_ms: u32,
    peak_lag: u32,
}

/// One queued Wintab packet. The serial is kept beside the bytes because the
/// application's packet mask may not include PK_SERIAL_NUMBER.
struct QueuedPacket {
    serial: u32,
    bytes: Vec<u8>,
}

struct Registry {
    slots: Vec<Option<Context>>,
    next: usize,
}

/// Wintab message offsets relative to the context's message base.
const WT_PACKET_OFFSET: u32 = 0;
const WT_CTXOPEN_OFFSET: u32 = 1;
const WT_PROXIMITY_OFFSET: u32 = 5;
const WT_CSRCHANGE_OFFSET: u32 = 7;

struct Notif {
    hwnd: usize,
    msg_base: u32,
    offset: u32,
    serial: u32,
    ctx: usize,
    /// Cursor index, only used by WT_CSRCHANGE.
    cursor: u32,
}

fn notify(hwnd: usize, msg_base: u32, offset: u32, payload: u32, context: usize, cursor: u32) {
    if hwnd == 0 {
        return;
    }
    if unsafe { IsWindow(hwnd as HWND) } == 0 {
        return;
    }
    let message = msg_base.wrapping_add(offset);
    // The messages disagree on argument order in the Wintab spec, and Wacom's
    // samples rely on it:
    //   WT_PACKET     lParam = HCTX, wParam = packet serial
    //                 (`WTPacket((HCTX)lParam, wParam, &pkt)`)
    //   WT_PROXIMITY  wParam = HCTX, lParam = 1 in range / 0 out
    //   WT_CSRCHANGE  wParam = cursor index, lParam = packet serial
    let (wparam, lparam) = if offset == WT_PACKET_OFFSET {
        (payload as usize, context as isize)
    } else if offset == WT_CSRCHANGE_OFFSET {
        (cursor as usize, payload as isize)
    } else {
        (context, payload as isize)
    };
    unsafe {
        PostMessageW(hwnd as HWND, message, wparam, lparam);
    }
    MESSAGES_POSTED.fetch_add(1, Ordering::Relaxed);

    static POSTED: AtomicU32 = AtomicU32::new(0);
    let seen = POSTED.fetch_add(1, Ordering::Relaxed);
    if seen < 8 {
        diag(&format!(
            "MSG id=0x{:X} wparam=0x{:X} lparam=0x{:X} hwnd=0x{:X}",
            message, wparam, lparam, hwnd
        ));
    }
}

fn registry() -> &'static Mutex<Registry> {
    static REGISTRY: OnceLock<Mutex<Registry>> = OnceLock::new();
    REGISTRY.get_or_init(|| {
        Mutex::new(Registry {
            slots: vec![None],
            next: 1,
        })
    })
}

/// Every standard data item this shim can produce. Reported as DVC_PKTDATA
/// ("always available") and as the packet mask of the default context.
fn supported_mask() -> u32 {
    PK_CONTEXT
        | PK_STATUS
        | PK_TIME
        | PK_CHANGED
        | PK_SERIAL_NUMBER
        | PK_CURSOR
        | PK_BUTTONS
        | PK_X
        | PK_Y
        | PK_Z
        | PK_NORMAL_PRESSURE
        | PK_TANGENT_PRESSURE
        | PK_ORIENTATION
        | PK_ROTATION
}

/// The default context mask must be the full set, exactly like a hardware
/// driver's WTI_DEFCONTEXT. Applications such as PaintTool SAI do not build a
/// mask of their own: they copy the default one and hand it back to WTOpen,
/// while reading fields at the offsets of the compile-time PACKET struct. A
/// trimmed default therefore delivers a short packet to an app that expects the
/// full 80-byte one, and every field after the missing item is read one field
/// early - pressure lands in the gap and reads as zero.
fn default_mask() -> u32 {
    supported_mask()
}

/// Digitizer extent taken from the shared state written by the OpenTabletDriver
/// plugin. The shim used to advertise the constants below no matter which tablet
/// was attached, so a device with a different resolution mapped its strokes
/// through the wrong in/out extents.
fn device_extent() -> (i32, i32) {
    if let Some(state) = shared() {
        let w = unsafe { ptr::read_volatile(&state.width) };
        let h = unsafe { ptr::read_volatile(&state.height) };
        if w > 0 && h > 0 {
            return (w, h);
        }
    }
    (MAX_X, MAX_Y)
}

fn packet_y(pen_y: i32) -> i32 {
    device_extent().1.saturating_sub(pen_y)
}

/// X coordinate for one packet. With `space.screen_px` the value already is a
/// screen pixel inside the configured mapping region; the application will
/// multiply it by (its cached) lcSysExt / digitizer extent, so divide that
/// factor back out and the result lands on the same pixel.
fn packet_x(x: i32, space: PacketSpace) -> i32 {
    if !space.screen_px || space.sys_ext[0] <= 0 {
        return x;
    }
    let (dev_w, _) = device_extent();
    ((x as i64 * dev_w as i64) / space.sys_ext[0] as i64).clamp(0, dev_w as i64) as i32
}

/// Y is the same conversion plus the bottom-up flip Wintab expects: the plugin
/// works in top-down screen pixels, the packet has to carry the distance from
/// the bottom of the *virtual desktop*.
fn packet_y_space(y: i32, space: PacketSpace) -> i32 {
    if !space.screen_px || space.sys_ext[1] <= 0 {
        return packet_y(y);
    }
    let (_, dev_h) = device_extent();
    let desktop_bottom = screen_origin()[1] + screen_extent()[1];
    let bottom_up = (desktop_bottom - y) as i64;
    ((bottom_up * dev_h as i64) / space.sys_ext[1] as i64).clamp(0, dev_h as i64) as i32
}

/// The desktop an application maps through lcSysExt/lcSysOrg. A hardware
/// driver reports the *virtual desktop*, not the primary monitor: with a second
/// screen attached the session desktop is wider and can start left of the
/// primary, and PaintTool SAI maps every packet through this rectangle.
///
/// Measured from the monitor topology (union of every monitor's rectangle)
/// instead of SM_CXSCREEN, cached, and re-measured while an application is
/// open - plugging a screen in has to be picked up without restarting the app.
#[repr(C)]
#[derive(Clone, Copy, PartialEq, Eq, Default)]
struct DisplayRect {
    left: i32,
    top: i32,
    right: i32,
    bottom: i32,
}

#[repr(C)]
struct MonitorInfoExW {
    cb_size: u32,
    rc_monitor: DisplayRect,
    rc_work: DisplayRect,
    flags: u32,
    device: [u16; 32],
}

const MONITORINFOF_PRIMARY: u32 = 1;

type MonitorEnumProc =
    unsafe extern "system" fn(*mut c_void, *mut c_void, *const DisplayRect, *mut c_void) -> i32;

extern "system" {
    fn EnumDisplayMonitors(
        hdc: *mut c_void,
        clip: *const DisplayRect,
        callback: MonitorEnumProc,
        data: *mut c_void,
    ) -> i32;
    fn GetMonitorInfoW(monitor: *mut c_void, info: *mut MonitorInfoExW) -> i32;
}

/// One enumerated monitor, kept for the log so the detected layout is named.
struct MonitorSnapshot {
    rect: DisplayRect,
    primary: bool,
    name: [u16; 32],
}

#[derive(Clone, Copy, PartialEq, Eq)]
struct DisplayState {
    left: i32,
    top: i32,
    width: i32,
    height: i32,
    count: u32,
}

static DISPLAY: Mutex<Option<DisplayState>> = Mutex::new(None);

unsafe extern "system" fn collect_monitor(
    monitor: *mut c_void,
    _hdc: *mut c_void,
    _rect: *const DisplayRect,
    data: *mut c_void,
) -> i32 {
    let list = &mut *(data as *mut Vec<MonitorSnapshot>);
    let mut info = MonitorInfoExW {
        cb_size: std::mem::size_of::<MonitorInfoExW>() as u32,
        rc_monitor: DisplayRect::default(),
        rc_work: DisplayRect::default(),
        flags: 0,
        device: [0; 32],
    };
    if !monitor.is_null() && GetMonitorInfoW(monitor, &mut info) != 0 {
        list.push(MonitorSnapshot {
            rect: info.rc_monitor,
            primary: info.flags & MONITORINFOF_PRIMARY != 0,
            name: info.device,
        });
    }
    1
}

fn monitor_snapshots() -> Vec<MonitorSnapshot> {
    let mut list: Vec<MonitorSnapshot> = Vec::new();
    unsafe {
        EnumDisplayMonitors(
            ptr::null_mut(),
            ptr::null(),
            collect_monitor,
            &mut list as *mut Vec<MonitorSnapshot> as *mut c_void,
        );
    }
    list
}

fn display_name(name: &[u16; 32]) -> String {
    let len = name.iter().position(|&c| c == 0).unwrap_or(name.len());
    String::from_utf16_lossy(&name[..len])
}

/// Union of every monitor rectangle; falls back to the primary screen when the
/// enumeration comes back empty (driver restart during a mode change).
fn measure_display() -> (DisplayState, Vec<MonitorSnapshot>) {
    let monitors = monitor_snapshots();
    if monitors.is_empty() {
        let w = unsafe { GetSystemMetrics(0) };
        let h = unsafe { GetSystemMetrics(1) };
        let (w, h) = if w > 0 && h > 0 { (w, h) } else { (1920, 1080) };
        return (
            DisplayState {
                left: 0,
                top: 0,
                width: w,
                height: h,
                count: 1,
            },
            monitors,
        );
    }

    let mut left = i32::MAX;
    let mut top = i32::MAX;
    let mut right = i32::MIN;
    let mut bottom = i32::MIN;
    for monitor in &monitors {
        left = left.min(monitor.rect.left);
        top = top.min(monitor.rect.top);
        right = right.max(monitor.rect.right);
        bottom = bottom.max(monitor.rect.bottom);
    }

    (
        DisplayState {
            left,
            top,
            width: (right - left).max(1),
            height: (bottom - top).max(1),
            count: monitors.len() as u32,
        },
        monitors,
    )
}

/// Re-measures the desktop, and when the topology changed logs what was found:
/// monitor count, the virtual desktop rectangle, and each screen by device name,
/// resolution, position and primary flag. Returns true when it changed.
fn refresh_display() -> bool {
    let (next, monitors) = measure_display();
    let previous = {
        let mut slot = DISPLAY.lock().unwrap_or_else(|e| e.into_inner());
        let previous = *slot;
        *slot = Some(next);
        previous
    };

    if previous == Some(next) {
        return false;
    }

    let mut line = format!(
        "DISPLAY monitors={} desktop={}x{} origin=({},{})",
        next.count, next.width, next.height, next.left, next.top
    );
    for (index, monitor) in monitors.iter().enumerate() {
        line.push_str(&format!(
            " | [{}] {} {}x{} at ({},{}){}",
            index,
            display_name(&monitor.name),
            monitor.rect.right - monitor.rect.left,
            monitor.rect.bottom - monitor.rect.top,
            monitor.rect.left,
            monitor.rect.top,
            if monitor.primary { " primary" } else { "" }
        ));
    }
    diag(&line);
    true
}

fn desktop_state() -> DisplayState {
    if let Some(state) = *DISPLAY.lock().unwrap_or_else(|e| e.into_inner()) {
        return state;
    }

    refresh_display();
    (*DISPLAY.lock().unwrap_or_else(|e| e.into_inner())).unwrap_or(DisplayState {
        left: 0,
        top: 0,
        width: 1920,
        height: 1080,
        count: 1,
    })
}

fn screen_extent() -> [i32; 2] {
    let state = desktop_state();
    [state.width, state.height]
}

fn screen_origin() -> [i32; 2] {
    let state = desktop_state();
    [state.left, state.top]
}
fn base_context_a() -> LogContextA {
    let (ext_x, ext_y) = device_extent();
    let mut ctx = LogContextA {
        name: [0; 40],
        options: CXO_SYSTEM | CXO_PEN,
        status: 0,
        locks: 0,
        msg_base: 0x7FF0,
        device: 0,
        pkt_rate: PKT_RATE,
        pkt_data: default_mask(),
        pkt_mode: TPS_NONE,
        move_mask: PK_X | PK_Y | PK_NORMAL_PRESSURE,
        btn_dn_mask: 0,
        btn_up_mask: 0,
        in_org: [0; 3],
        in_ext: [ext_x, ext_y, 1],
        out_org: [0; 3],
        out_ext: [ext_x, ext_y, 1],
        sens: [0; 3],
        sys_mode: 0,
        sys_org: screen_origin(),
        sys_ext: screen_extent(),
        sys_sens: [65536, 65536],
    };
    let bytes = b"OpenTabletDriver Pen";
    ctx.name[..bytes.len()].copy_from_slice(bytes);
    ctx
}

fn base_context_w() -> LogContextW {
    let (ext_x, ext_y) = device_extent();
    let mut ctx = LogContextW {
        name: [0; 40],
        options: CXO_SYSTEM | CXO_PEN,
        status: 0,
        locks: 0,
        msg_base: 0x7FF0,
        device: 0,
        pkt_rate: PKT_RATE,
        pkt_data: default_mask(),
        pkt_mode: TPS_NONE,
        move_mask: PK_X | PK_Y | PK_NORMAL_PRESSURE,
        btn_dn_mask: 0,
        btn_up_mask: 0,
        in_org: [0; 3],
        in_ext: [ext_x, ext_y, 1],
        out_org: [0; 3],
        out_ext: [ext_x, ext_y, 1],
        sens: [0; 3],
        sys_mode: 0,
        sys_org: screen_origin(),
        sys_ext: screen_extent(),
        sys_sens: [65536, 65536],
    };
    let units: Vec<u16> = "OpenTabletDriver Pen".encode_utf16().collect();
    ctx.name[..units.len()].copy_from_slice(&units);
    ctx
}

// ---------------------------------------------------- synthetic pen pointer
//
// Once the Wacom HID driver is gone Windows has no digitizer at all: the only
// pointer in the system is the mouse OTD injects. Applications that take
// pressure from the system pointer (Photoshop, PaintTool SAI) then move and
// click correctly but never see pressure, no matter what the Wintab packets
// say. CreateSyntheticPointerDevice puts a real PEN pointer back into the
// system so those applications receive pointer messages carrying pressure.

#[repr(C)]
#[derive(Clone, Copy, Default)]
struct PointerInfo {
    pointer_type: i32,
    pointer_id: u32,
    frame_id: u32,
    pointer_flags: u32,
    source_device: usize,
    hwnd_target: usize,
    pt_pixel_location: [i32; 2],
    pt_himetric_location: [i32; 2],
    pt_pixel_location_raw: [i32; 2],
    pt_himetric_location_raw: [i32; 2],
    dw_time: u32,
    history_count: u32,
    input_data: i32,
    key_states: u32,
    performance_count: u64,
    button_change_type: i32,
    _reserved: i32,
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
struct PointerPenInfo {
    info: PointerInfo,
    pen_flags: u32,
    pen_mask: u32,
    pressure: u32,
    rotation: u32,
    tilt_x: i32,
    tilt_y: i32,
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
struct PointerTypeInfo {
    kind: i32,
    _padding: i32,
    pen: PointerPenInfo,
}

const PT_PEN: i32 = 3;
const PEN_MASK_PRESSURE: u32 = 0x0000_0001;
const POINTER_FLAG_INRANGE: u32 = 0x0000_0002;
const POINTER_FLAG_INCONTACT: u32 = 0x0000_0004;
const POINTER_FLAG_FIRSTBUTTON: u32 = 0x0000_0010;
const POINTER_FLAG_PRIMARY: u32 = 0x0000_2000;
const POINTER_FLAG_DOWN: u32 = 0x0001_0000;
const POINTER_FLAG_UPDATE: u32 = 0x0002_0000;
const POINTER_FLAG_UP: u32 = 0x0004_0000;

#[link(name = "user32")]
extern "system" {
    fn CreateSyntheticPointerDevice(pointer_type: i32, max_count: u32, mode: u32) -> isize;
    fn InjectSyntheticPointerInput(device: isize, infos: *const PointerTypeInfo, count: u32) -> i32;
    fn DestroySyntheticPointerDevice(device: isize);
    fn GetSystemMetrics(index: i32) -> i32;
}

struct PenInjectState {
    device: isize,
    tried: bool,
    pressed: bool,
    in_range: bool,
    last_x: i32,
    last_y: i32,
    last_pressure: u32,
    frame: u32,
}

fn pen_inject_state() -> &'static Mutex<PenInjectState> {
    static STATE: OnceLock<Mutex<PenInjectState>> = OnceLock::new();
    STATE.get_or_init(|| {
        Mutex::new(PenInjectState {
            device: 0,
            tried: false,
            pressed: false,
            in_range: false,
            last_x: i32::MIN,
            last_y: i32::MIN,
            last_pressure: 0,
            frame: 0,
        })
    })
}

/// File switch: `%LOCALAPPDATA%\OTDWintab\inject-pen.txt` containing `1` turns
/// the synthetic PEN pointer on. Off by default so a machine without the
/// Wintab workload keeps behaving exactly as before.
fn pen_injection_enabled() -> bool {
    static CACHE: AtomicU32 = AtomicU32::new(0);
    static LAST_CHECK: AtomicU32 = AtomicU32::new(0);
    let now = unsafe { GetTickCount() };
    if now.wrapping_sub(LAST_CHECK.load(Ordering::Relaxed)) >= 1000 {
        LAST_CHECK.store(now, Ordering::Relaxed);
        let enabled = std::env::var("LOCALAPPDATA")
            .ok()
            .and_then(|dir| std::fs::read_to_string(format!("{dir}\\OTDWintab\\inject-pen.txt")).ok())
            .map(|text| text.trim().starts_with('1'))
            .unwrap_or(false);
        CACHE.store(if enabled { 1 } else { 0 }, Ordering::Relaxed);
    }
    CACHE.load(Ordering::Relaxed) != 0
}

/// Pushes the newest shared-memory sample into the synthetic pen. Coordinates
/// are converted from tablet units to virtual-desktop pixels, pressure from the
/// 0..32767 Wintab range to the 0..1024 Ink range.
fn inject_pen_tick() {
    if !pen_injection_enabled() {
        return;
    }
    let Some(shared_state) = shared() else { return };
    let index = shared_state.write_index.load(Ordering::Acquire);
    if index == 0 {
        return;
    }
    let slot = ((index - 1) as usize) % RING_CAPACITY;
    let sample = unsafe { ptr::read_volatile(&shared_state.packets[slot]) };
    let (max_x, max_y) = device_extent();

    let vx = unsafe { GetSystemMetrics(76) };
    let vy = unsafe { GetSystemMetrics(77) };
    let vw = unsafe { GetSystemMetrics(78) };
    let vh = unsafe { GetSystemMetrics(79) };
    if vw <= 0 || vh <= 0 || max_x <= 1 || max_y <= 1 {
        return;
    }

    let px = vx + ((sample.x as i64 * vw as i64) / max_x as i64) as i32;
    let py = vy + ((sample.y as i64 * vh as i64) / max_y as i64) as i32;
    let in_range = sample.in_range != 0;
    let pressed = in_range && (sample.buttons & 1) != 0;
    let pressure = if pressed {
        ((sample.pressure.min(RAW_PRESSURE_MAX) as u64 * 1024) / RAW_PRESSURE_MAX as u64) as u32
    } else {
        0
    };

    let mut st = pen_inject_state().lock().unwrap_or_else(|e| e.into_inner());
    if !st.tried {
        st.tried = true;
        let device = unsafe { CreateSyntheticPointerDevice(PT_PEN, 1, 1) };
        if device == 0 {
            diag("inject: CreateSyntheticPointerDevice failed");
            return;
        }
        st.device = device;
        diag("inject: synthetic PEN pointer created");
    }

    let unchanged = st.in_range == in_range
        && st.pressed == pressed
        && st.last_x == px
        && st.last_y == py
        && st.last_pressure == pressure;
    if unchanged {
        return;
    }

    let mut flags = 0u32;
    let mut change = 0i32;
    if pressed && !st.pressed {
        flags = POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT | POINTER_FLAG_FIRSTBUTTON
            | POINTER_FLAG_PRIMARY | POINTER_FLAG_DOWN;
        change = 1; // POINTER_CHANGE_FIRSTBUTTON_DOWN
    } else if pressed {
        flags = POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT | POINTER_FLAG_FIRSTBUTTON
            | POINTER_FLAG_UPDATE;
    } else if st.pressed {
        flags = POINTER_FLAG_INRANGE | POINTER_FLAG_FIRSTBUTTON | POINTER_FLAG_UP;
        change = 2; // POINTER_CHANGE_FIRSTBUTTON_UP
    } else if in_range {
        flags = POINTER_FLAG_INRANGE | POINTER_FLAG_UPDATE;
    } else if st.in_range {
        flags = POINTER_FLAG_UPDATE;
    } else {
        return;
    }

    st.pressed = pressed;
    st.in_range = in_range;
    st.last_x = px;
    st.last_y = py;
    st.last_pressure = pressure;
    st.frame = st.frame.wrapping_add(1);

    // Windows derives cursor position from the himetric (0.01 mm) pair as well
    // as the pixel pair. Leaving himetric at zero made every frame alternate
    // between the pen position and the digitizer origin, which showed up as a
    // cursor flickering at full speed.
    let hx = ((sample.x.max(0) as i64 * 32767) / max_x as i64).clamp(0, 32767) as i32;
    let hy = ((sample.y.max(0) as i64 * 32767) / max_y as i64).clamp(0, 32767) as i32;

    let mut info = PointerTypeInfo::default();
    info.kind = PT_PEN;
    info.pen.info.pointer_type = PT_PEN;
    info.pen.info.pointer_id = 1;
    info.pen.info.frame_id = st.frame;
    info.pen.info.pointer_flags = flags;
    info.pen.info.pt_pixel_location = [px, py];
    info.pen.info.pt_himetric_location = [hx, hy];
    info.pen.info.pt_pixel_location_raw = [px, py];
    info.pen.info.pt_himetric_location_raw = [hx, hy];
    info.pen.info.dw_time = unsafe { GetTickCount() };
    info.pen.info.button_change_type = change;
    info.pen.pen_mask = PEN_MASK_PRESSURE;
    info.pen.pressure = pressure;
    info.pen.tilt_x = sample.tilt_x;
    info.pen.tilt_y = sample.tilt_y;
    unsafe {
        InjectSyntheticPointerInput(st.device, &info, 1);
    }
}

/// Background pump: mirrors new shared-memory samples into WT_PACKET messages
/// for message-driven applications, which never call WTPacketsGet on their own.
fn start_notifier() {
    use std::sync::atomic::{AtomicBool, Ordering as AtomicOrdering};
    static STARTED: AtomicBool = AtomicBool::new(false);
    if STARTED.swap(true, AtomicOrdering::SeqCst) {
        return;
    }

    std::thread::spawn(|| {
        let mut display_check = unsafe { GetTickCount() };
        loop {
            // 4 ms is 250 Hz: one notifier tick for every packet a 200 Hz tablet
            // can produce. Polling at 2 ms only let the shim outrun the
            // application and pile messages on its queue.
            std::thread::sleep(std::time::Duration::from_millis(4));
            let now = unsafe { GetTickCount() };

            // A screen plugged in or unplugged must be seen without restarting
            // the painting application: re-measure the desktop once a second,
            // atomically enough that new contexts read the new rectangle.
            if now.wrapping_sub(display_check) >= 1000 {
                display_check = now;
                refresh_display();
            }
            let targets: Vec<Notif> = {
                let mut reg = registry().lock().unwrap_or_else(|e| e.into_inner());
                let mut out = Vec::new();
                for (index, slot) in reg.slots.iter_mut().enumerate() {
                    let Some(ctx) = slot.as_mut() else { continue };
                    if ctx.hwnd == 0 || !ctx.enabled {
                        continue;
                    }
                    // Only signal when the queue transitions from empty to
                    // non-empty; the application drains it with WTPacketsGet.
                    // Poll the shared ring from here only for applications that
                    // do not poll it themselves. Everything below runs under the
                    // registry lock, and a busy application enters the same lock
                    // tens of thousands of times a second while painting; draining
                    // the ring on every notifier tick as well made the two sides
                    // take turns on the mutex and produced the occasional visible
                    // stall. When the application has consumed within the last
                    // few milliseconds it is keeping up on its own, so stay off
                    // the lock entirely.
                    if now.wrapping_sub(ctx.last_consume_ms) > 10 {
                        refresh(ctx, index);
                    }

                    // Applications only initialise pen capabilities (pressure,
                    // tilt) after a WT_PROXIMITY message announces that the tool
                    // entered range, so emit it on every in/out transition.
                    if ctx.pen.in_range != ctx.proximity_sent {
                        ctx.proximity_sent = ctx.pen.in_range;
                        let entering = ctx.pen.in_range;
                        if entering {
                            ctx.csr_change_pending = true;
                        }
                        out.push(Notif {
                            hwnd: ctx.hwnd,
                            msg_base: ctx.msg_base,
                            offset: WT_PROXIMITY_OFFSET,
                            serial: if entering { 1 } else { 0 },
                            ctx: index,
                            cursor: ctx.csr_cursor,
                        });
                    }

                    // Newest packet the driver has produced, whether or not the
                    // application has already drained it. Deciding "is there
                    // anything to announce?" from the queue alone starves an
                    // application that polls fast: it empties the queue between
                    // two notifier ticks, so the tick keeps seeing nothing
                    // pending and never posts WT_PACKET at all. Photoshop then
                    // paints a single dot on the press message and never receives
                    // the motion messages that drive the stroke.
                    let pending_serial = if ctx.queue.len() > ctx.read {
                        ctx.queue.last().unwrap().serial
                    } else {
                        ctx.produced_serial
                    };
                    let has_pending = pending_serial != 0;
                    // Old applications bind the cursor's pressure capability when
                    // the driver announces WT_CSRCHANGE, then answer WT_PACKET.
                    // Emit it once per proximity entry (and on a real cursor
                    // switch), before the packet that follows.
                    if has_pending && ctx.pen.in_range && ctx.csr_change_pending {
                        ctx.csr_change_pending = false;
                        out.push(Notif {
                            hwnd: ctx.hwnd,
                            msg_base: ctx.msg_base,
                            offset: WT_CSRCHANGE_OFFSET,
                            serial: pending_serial,
                            ctx: index,
                            cursor: ctx.csr_cursor,
                        });
                    }
                    if has_pending {
                        // Notify with the NEWEST serial. The application answers
                        // WT_PACKET with WTPacket(serial) and expects the packet
                        // the driver just produced. Posting the oldest queued
                        // serial instead makes a slow consumer chase its own
                        // tail: every message resolves to a sample from seconds
                        // ago, so coordinates keep up barely and pressure is
                        // whatever the very first packet had.
                        //
                        // At most one message per notifier tick (4 ms). WTPacket
                        // does not advance the read cursor, so a rule that waits
                        // for a drain would starve an app that only ever answers
                        // WT_PACKET; a serial-change rule with a single post per
                        // tick keeps the stream moving without the flood the
                        // 2 ms loop produced.
                        if pending_serial != ctx.last_notified_serial {
                            // A switched-away window keeps accepting messages
                            // while its message loop is frozen. Posting at the
                            // full 250 Hz for the whole time the user works elsewhere
                            // queues hundreds of WT_PACKETs, and by the time the
                            // window processes them the queue here has already
                            // replaced every referenced packet, so each answer
                            // resolves to a miss: no coordinates, no pressure,
                            // and a lone dot on the next press. Post at full rate
                            // only while the application is actually consuming;
                            // while it is away, leave one message every 200 ms as
                            // a heartbeat so it learns there is data when it
                            // resumes without finding a backlog in front of it.
                            let away = now.wrapping_sub(ctx.last_consume_ms) > CONSUMER_IDLE_MS;
                            if away && now.wrapping_sub(ctx.last_notify_ms) < CONSUMER_AWAY_INTERVAL_MS {
                                ctx.suppressed_posts = ctx.suppressed_posts.saturating_add(1);
                            } else {
                                ctx.last_notified_serial = pending_serial;
                                ctx.last_notify_ms = now;
                                out.push(Notif {
                                    hwnd: ctx.hwnd,
                                    msg_base: ctx.msg_base,
                                    offset: WT_PACKET_OFFSET,
                                    serial: pending_serial,
                                    ctx: index,
                                    cursor: ctx.csr_cursor,
                                });
                            }
                        }
                    }
                }
                out
            };

            for n in targets {
                notify(n.hwnd, n.msg_base, n.offset, n.serial, n.ctx, n.cursor);
            }

            inject_pen_tick();
        }
    });
}

/// OTD's platform pointer already injects real mouse clicks, so the shim must
/// not inject a second one — that turned every tap into a double click.
const INJECT_SYSTEM_BUTTONS: bool = false;

/// Real Wintab drivers own the system cursor while a CXO_SYSTEM context is open,
/// so a tip press has to produce an actual mouse click as well. Without this the
/// pen only works inside applications that read Wintab packets — menus, dialogs
/// and every ordinary window ignore it, which looks like "the pen can't click".
fn inject_system_buttons(old: u32, new: u32) {
    const LEFT: u32 = 1;    const RIGHT: u32 = 2;
    const MIDDLE: u32 = 4;
    let down = new & !old;
    let up = old & !new;
    unsafe {
        if down & LEFT != 0 {
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
        }
        if up & LEFT != 0 {
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
        }
        if down & RIGHT != 0 {
            mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, 0);
        }
        if up & RIGHT != 0 {
            mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, 0);
        }
        if down & MIDDLE != 0 {
            mouse_event(MOUSEEVENTF_MIDDLEDOWN, 0, 0, 0, 0);
        }
        if up & MIDDLE != 0 {
            mouse_event(MOUSEEVENTF_MIDDLEUP, 0, 0, 0, 0);
        }
    }
}

fn open_context(
    packet_data: u32,
    packet_mode: u32,
    enable: bool,
    hwnd: usize,
    msg_base: u32,
    options: u32,
    sys_org: [i32; 2],
    sys_ext: [i32; 2],
) -> *mut c_void {
    let mask = if packet_data == 0 { default_mask() } else { packet_data };
    let (width, slots) = packet_layout(mask);
    let pressure_at = slots
        .iter()
        .find(|(bit, _)| *bit == PK_NORMAL_PRESSURE)
        .map(|(_, at)| *at);
    diag(&format!(
        "WTOpen pkt_data=0x{:X} pkt_mode=0x{:X} options=0x{:X} enable={} hwnd=0x{:X} msg_base=0x{:X} width={} pressure_at={:?} build={}",
        mask, packet_mode, options, enable, hwnd, msg_base, width, pressure_at, SHIM_BUILD
    ));

    // Applications read lcSysExt once, at startup, and never refresh it. Logging
    // the value they brought tells us whether a wrong stroke position comes from
    // a stale rectangle (dual screen cached, single screen now) or from our own
    // conversion.
    let current = screen_extent();
    diag(&format!(
        "WTOpen space: client_sys_ext={}x{} current_screen={}x{} client_sys_org=({},{}) screen_org=({},{}) screen_px={}",
        sys_ext[0], sys_ext[1], current[0], current[1],
        sys_org[0], sys_org[1], screen_origin()[0], screen_origin()[1], screen_space()
    ));
    let mut reg = registry().lock().unwrap_or_else(|e| e.into_inner());
    let handle = reg.next;
    reg.next += 1;
    while reg.slots.len() <= handle {
        reg.slots.push(None);
    }

    let mut pen = PenState::default();
    poll_shared(&mut pen);

    reg.slots[handle] = Some(Context {
        packet_data: mask,
        packet_mode,
        sys_ext,
        sys_org,
        pen,
        queue: Vec::with_capacity(64),
        read: 0,
        enabled: enable,
        hwnd,
        msg_base: if msg_base == 0 { 0x7FF0 } else { msg_base },
        last_notified_serial: 0,
        proximity_sent: false,
        csr_change_pending: true,
        csr_cursor: PEN_CURSOR_INDEX,
        peak_pressure: 0,
        peak_samples: 0,
        peak_packed_pressure: 0,
        peak_window_start: unsafe { GetTickCount() },
        options,
        system_buttons: 0,
        feel_smoothed: 0.0,
        get_calls: 0,
        produced_serial: 0,
        last_consume_ms: unsafe { GetTickCount() },
        last_notify_ms: 0,
        suppressed_posts: 0,
        last_sample_ms: 0,
        peak_age_ms: 0,
        peak_lag: 0,
    });

    drop(reg);
    start_notifier();

    // A driver announces WT_CTXOPEN right after the context is enabled; message-
    // driven applications use it to start their update loop.
    if hwnd != 0 && options & CXO_MESSAGES != 0 {
        let base = if msg_base == 0 { 0x7FF0 } else { msg_base };
        notify(hwnd, base, WT_CTXOPEN_OFFSET, 1, handle, 0);
    }

    handle as *mut c_void
}

fn with_context<T>(handle: *mut c_void, f: impl FnOnce(&mut Context) -> T) -> Option<T> {
    let index = handle as usize;
    let mut reg = registry().lock().unwrap_or_else(|e| e.into_inner());
    if index == 0 || index >= reg.slots.len() {
        return None;
    }
    reg.slots[index].as_mut().map(f)
}

// ---------------------------------------------------------------- encoding

/// Wintab lays a packet out as a compacted struct: only the members named in
/// the context's `lcPktData` are present, in the order `pktdef.h` declares
/// them. Applications compute their buffer size and field offsets from that
/// same mask, so the layout MUST follow the mask the app passed to WTOpen.
/// Field sizes in pktdef.h order; PK_CONTEXT is pointer-sized.

/// Byte offset of every mask member, plus the total packet width.
///
/// Width follows pktdef.h's conditional struct: members the mask does not name
/// are absent, and a struct containing HCTX inherits pointer alignment, so the
/// total is padded to that boundary (x64 full packet = 80 bytes).
fn packet_layout(mask: u32) -> (usize, Vec<(u32, usize)>) {
    let align = std::mem::align_of::<usize>();
    let mut offset = 0usize;
    let mut slots = Vec::with_capacity(8);

    // Wintab's PACKET is a flexible struct: only the fields whose bits are set
    // in lcPktData are physically present, in bit order. Photoshop's 0x1FFC
    // does not include PK_STATUS, so its first field is pkTime. Inserting a
    // pkStatus that the mask did not ask for shifts every later field by four
    // bytes — PS then reads pkZ as pkNormalPressure and pkCursor as pkButtons,
    // which is exactly "no pressure and wrong clicks". Build the struct from
    // the mask, with no app-specific shape.
    let mut push = |bit: u32, size: usize| {
        let field_align = if size >= 8 && bit == PK_CONTEXT { align } else { 4 };
        offset = (offset + field_align - 1) & !(field_align - 1);
        slots.push((bit, offset));
        offset += size;
    };

    if mask & PK_CONTEXT != 0 {
        push(PK_CONTEXT, std::mem::size_of::<usize>());
    }
    if mask & PK_STATUS != 0 { push(PK_STATUS, 4); }
    if mask & PK_TIME != 0 { push(PK_TIME, 4); }
    if mask & PK_CHANGED != 0 { push(PK_CHANGED, 4); }
    if mask & PK_SERIAL_NUMBER != 0 { push(PK_SERIAL_NUMBER, 4); }
    if mask & PK_CURSOR != 0 { push(PK_CURSOR, 4); }
    if mask & PK_BUTTONS != 0 { push(PK_BUTTONS, 4); }
    if mask & PK_X != 0 { push(PK_X, 4); }
    if mask & PK_Y != 0 { push(PK_Y, 4); }
    if mask & PK_Z != 0 { push(PK_Z, 4); }
    if mask & PK_NORMAL_PRESSURE != 0 { push(PK_NORMAL_PRESSURE, 4); }
    if mask & PK_TANGENT_PRESSURE != 0 { push(PK_TANGENT_PRESSURE, 4); }
    if mask & PK_ORIENTATION != 0 { push(PK_ORIENTATION, 12); }
    if mask & PK_ROTATION != 0 { push(PK_ROTATION, 12); }

    let total_align = if mask & PK_CONTEXT != 0 && std::mem::size_of::<usize>() == 8 {
        8
    } else {
        4
    };
    let total = (offset + total_align - 1) & !(total_align - 1);
    (total, slots)
}

fn hexline(b: &[u8]) -> String {
    let mut s = String::with_capacity(b.len() * 3);
    for x in b.iter().take(b.len()) {
        s.push_str(&format!("{:02X}", x));
    }
    s
}

fn put_u32(buf: &mut [u8], offset: usize, value: u32) {
    if offset + 4 <= buf.len() {
        buf[offset..offset + 4].copy_from_slice(&value.to_le_bytes());
    }
}

fn put_i32(buf: &mut [u8], offset: usize, value: i32) {
    if offset + 4 <= buf.len() {
        buf[offset..offset + 4].copy_from_slice(&value.to_le_bytes());
    }
}

/// Wintab serials must be unique and increasing. The daemon's sequence number
/// identifies each sample exactly, so use it when present and fall back to a
/// local counter only for synthetic packets.
fn next_serial(current: u32, _sequence: u32) -> u32 {
    // Strictly +1 per queued packet, never the shared-memory sequence.
    // Applications such as SAI walk serials one by one between the value they
    // last saw and the one in WT_PACKET. Reusing OTD's sequence leaves gaps
    // whenever a sample is skipped, and every gap costs the app a WTPacket
    // call that returns nothing - thousands of calls per second, which is
    // exactly what makes the brush lag.
    current.wrapping_add(1)
}

/// TPS_PROXIMITY from wintab.h (0x0001). The old value 0x0004 is TPS_MARGIN,
/// so in-range packets claimed the pen was in the margin — and out-of-range
/// packets claimed a perfectly healthy status.
const TPS_PROXIMITY: u32 = 0x0001;

/// Reads a 32-bit field out of an already serialized packet, using the same
/// layout the serializer used.
fn packet_field(mask: u32, bytes: &[u8], bit: u32) -> u32 {
    packet_layout(mask)
        .1
        .iter()
        .find(|(slot, _)| *slot == bit)
        .and_then(|(_, at)| bytes.get(*at..*at + 4))
        .map(|s| u32::from_le_bytes([s[0], s[1], s[2], s[3]]))
        .unwrap_or(0)
}

/// Reads pkNormalPressure out of an already serialized packet.
fn packet_pressure(mask: u32, bytes: &[u8]) -> u32 {
    packet_field(mask, bytes, PK_NORMAL_PRESSURE)
}

/// The pressure ceiling actually published by the OTD plugin. The old C# plugin
/// in the portable userdata directory publishes 0..8191 while the shim advertises
/// DVC_NPRESSURE.axMax = 32767. Treating those as the same range caps a full
/// press at 25% of the application's scale.
fn shared_pressure_max() -> u32 {
    shared()
        .map(|state| unsafe { ptr::read_volatile(&state.max_pressure) })
        .filter(|max| *max > 0)
        .unwrap_or(RAW_PRESSURE_MAX)
        .min(RAW_PRESSURE_MAX)
}

/// Maps the shared buffer's raw pressure range onto Wintab's declared
/// normal-pressure range. Identity when the plugin already publishes 0..32767;
/// otherwise normalizes 0..8191 (or any other ceiling) to 0..32767.
fn scale_pressure(raw: u32) -> u32 {
    let max = shared_pressure_max();
    ((raw.min(max) as u64 * MAX_PRESSURE as u64) / max as u64) as u32
}

/// Tangent pressure has its own 0..1023 range in the official declaration.
fn scale_tangent_pressure(raw: u32) -> u32 {
    let max = shared_pressure_max();
    ((raw.min(max) as u64 * MAX_TANGENT_PRESSURE as u64) / max as u64) as u32
}

// ---------------------------------------------------------------- 手感 (feel)
/// Parameters written by the OTD 手感控制 panel into
/// `%LOCALAPPDATA%\OTDWintab\feel.json`. When the file is absent every packet
/// passes through untouched, so the panel has to be opened once before a curve
/// affects any application.
#[derive(Clone, Copy)]
struct FeelSettings {
    active: bool,
    dead_zone: f32,
    elasticity: f32,
    stroke_filter: u32,
    max_pressure: f32,
}

impl FeelSettings {
    const PASS_THROUGH: FeelSettings = FeelSettings {
        active: false,
        dead_zone: 0.0,
        elasticity: 1.0,
        stroke_filter: 1,
        max_pressure: 1.0,
    };
}

fn json_number(text: &str, key: &str) -> Option<f32> {
    let needle = format!("\"{key}\"");
    let start = text.find(&needle)? + needle.len();
    let rest = &text[start..];
    let colon = rest.find(':')?;
    let tail = rest[colon + 1..].trim_start();
    let end = tail
        .find(|c: char| c == ',' || c == '}' || c == '\n' || c == '\r')
        .unwrap_or(tail.len());
    tail[..end].trim().parse::<f32>().ok()
}

fn read_feel() -> FeelSettings {
    let Some(dir) = std::env::var_os("LOCALAPPDATA") else {
        return FeelSettings::PASS_THROUGH;
    };
    let path = std::path::PathBuf::from(dir).join("OTDWintab").join("feel.json");
    let Ok(text) = std::fs::read_to_string(&path) else {
        return FeelSettings::PASS_THROUGH;
    };
    // The panel's WinTab / Windows Ink switch decides whether this curve applies
    // at all: in Ink mode the Wintab context is not the active input path.
    let win_tab = text
        .find("\"InputMode\"")
        .map(|pos| {
            text.get(pos..pos + 64)
                .map(|slice| slice.to_ascii_lowercase().contains("wintab"))
                .unwrap_or(true)
        })
        .unwrap_or(true);
    if !win_tab {
        return FeelSettings::PASS_THROUGH;
    }
    FeelSettings {
        active: true,
        dead_zone: json_number(&text, "DeadZone").unwrap_or(0.0).clamp(0.0, 0.3),
        elasticity: json_number(&text, "Elasticity").unwrap_or(1.0).clamp(0.1, 10.0),
        stroke_filter: json_number(&text, "StrokeFilter").unwrap_or(1.0).clamp(1.0, 16.0) as u32,
        max_pressure: json_number(&text, "MaxPressure").unwrap_or(1.0).clamp(0.01, 1.0),
    }
}

fn feel() -> FeelSettings {
    static VALUE: OnceLock<Mutex<(FeelSettings, u32)>> = OnceLock::new();
    let cell = VALUE.get_or_init(|| Mutex::new((FeelSettings::PASS_THROUGH, 0)));
    let now = unsafe { GetTickCount() };
    let mut guard = cell.lock().unwrap_or_else(|e| e.into_inner());
    if now.wrapping_sub(guard.1) >= 1000 {
        guard.0 = read_feel();
        guard.1 = now;
    }
    guard.0
}

/// Dead zone -> peak normalisation -> elasticity curve -> stroke smoothing,
/// in the same order the reference implementation applies them.
fn apply_feel(context: &mut Context, raw: u32) -> u32 {
    let settings = feel();
    if !settings.active {
        context.feel_smoothed = 0.0;
        return raw;
    }
    let mut p = raw as f32 / RAW_PRESSURE_MAX as f32;
    if p <= settings.dead_zone || p <= 0.0 {
        context.feel_smoothed = 0.0;
        return 0;
    }
    let span = (settings.max_pressure - settings.dead_zone).max(0.0001);
    p = ((p - settings.dead_zone) / span).clamp(0.0, 1.0);

    let rise = p.powf(0.92);
    let fall = settings.elasticity * (1.0 - p).max(0.0).powf(1.19);
    let curved = if rise + fall > 0.0 { rise / (rise + fall) } else { 0.0 };

    let window = settings.stroke_filter.max(1);
    if window > 1 {
        let alpha = 1.0 / window as f32;
        context.feel_smoothed = context.feel_smoothed * (1.0 - alpha) + curved * alpha;
    } else {
        context.feel_smoothed = curved;
    }
    (context.feel_smoothed * RAW_PRESSURE_MAX as f32).round() as u32
}

/// Diagnostic switch: when `%LOCALAPPDATA%\OTDWintab\force-pressure.txt` contains
/// `1`, every packet reports maximum normal pressure. It separates "this
/// application never reads pkNormalPressure" from "it reads it at a different
/// offset / different range", which look identical from the packet log.
/// Off unless the file exists.
fn force_pressure() -> bool {
    static CACHE: AtomicU32 = AtomicU32::new(0);
    static LAST_CHECK: AtomicU32 = AtomicU32::new(0);
    let now = unsafe { GetTickCount() };
    if now.wrapping_sub(LAST_CHECK.load(Ordering::Relaxed)) >= 1000 {
        LAST_CHECK.store(now, Ordering::Relaxed);
        let enabled = std::env::var("LOCALAPPDATA")
            .ok()
            .and_then(|dir| std::fs::read_to_string(format!("{dir}\\OTDWintab\\force-pressure.txt")).ok())
            .map(|text| text.trim().starts_with('1'))
            .unwrap_or(false);
        CACHE.store(if enabled { 1 } else { 0 }, Ordering::Relaxed);
    }
    CACHE.load(Ordering::Relaxed) != 0
}

/// Diagnostic switch: `%LOCALAPPDATA%\OTDWintab\zero-xy.txt` containing `1`
/// forces pkX/pkY to 0 while leaving every other field intact. If a drawing
/// application still follows the real pen position with this on, it is not
/// drawing from Wintab packets at all - it reads the system pointer, and
/// pressure therefore has to come from a system pen, not from the packet
/// stream.
fn zero_xy() -> bool {
    static CACHE: AtomicU32 = AtomicU32::new(0);
    static LAST_CHECK: AtomicU32 = AtomicU32::new(0);
    let now = unsafe { GetTickCount() };
    if now.wrapping_sub(LAST_CHECK.load(Ordering::Relaxed)) >= 1000 {
        LAST_CHECK.store(now, Ordering::Relaxed);
        let enabled = std::env::var("LOCALAPPDATA")
            .ok()
            .and_then(|dir| std::fs::read_to_string(format!("{dir}\\OTDWintab\\zero-xy.txt")).ok())
            .map(|text| text.trim().starts_with('1'))
            .unwrap_or(false);
        CACHE.store(if enabled { 1 } else { 0 }, Ordering::Relaxed);
    }
    CACHE.load(Ordering::Relaxed) != 0
}

/// Coordinate space a single packet has to be encoded in.
///
/// `screen_px` means the plugin handed us pixels of the *configured mapping
/// region* instead of the digitizer's own units (flag: bit 31 of the shared
/// header's version field). The application still divides by the lcSysExt it
/// cached when it opened its context, so the pixels have to be converted back
/// into units that make that division land on the same screen pixel.
#[derive(Clone, Copy)]
struct PacketSpace {
    screen_px: bool,
    sys_ext: [i32; 2],
    sys_org: [i32; 2],
}

impl PacketSpace {
    fn device_units(self) -> PacketSpace {
        PacketSpace { screen_px: false, ..self }
    }
}

fn screen_space() -> bool {
    match shared() {
        Some(state) => (unsafe { ptr::read_volatile(&state.version) } & 0x8000_0000) != 0,
        None => false,
    }
}

fn serialize(mask: u32, pen: &PenState, context: usize, packet_mode: u32, space: PacketSpace) -> Vec<u8> {
    let (width, slots) = packet_layout(mask);
    let mut buf = vec![0u8; width.max(4)];
    let pressure = if force_pressure() { MAX_PRESSURE } else { scale_pressure(pen.pressure) };
    let tangent_pressure = scale_tangent_pressure(pen.pressure);

    // OTD tilt is in degrees, one per axis. Wintab wants azimuth (clockwise
    // from +x, in tenths of a degree), altitude (0 = perpendicular, 900 = flat)
    // and twist (0..3600, 1800 = normal orientation).
    let (tx, ty) = (pen.tilt_x as f32, pen.tilt_y as f32);
    let azimuth = ((ty.atan2(tx) * 1800.0 / std::f32::consts::PI).round() as i32)
        .rem_euclid(3600);
    let altitude = (900.0 - tx.hypot(ty) * 10.0).round().clamp(0.0, 900.0) as i32;
    let twist = ((pen.rotation.clamp(-900, 900) + 900) * 2) as i32;

    for (bit, at) in slots {
        match bit {
            PK_CONTEXT => {
                if std::mem::size_of::<usize>() == 8 {
                    if at + 8 <= buf.len() {
                        buf[at..at + 8].copy_from_slice(&(context as u64).to_le_bytes());
                    }
                } else if at + 4 <= buf.len() {
                    buf[at..at + 4].copy_from_slice(&(context as u32).to_le_bytes());
                }
            }
            // TPS_PROXIMITY means the cursor is OUT of the context, so an
            // in-range packet must report zero status. The old inverse made
            // every app believe the pen had left the tablet.
            PK_STATUS => put_u32(
                &mut buf,
                at,
                if pen.in_range && !FORCE_PROX_STATUS.load(Ordering::Relaxed) {
                    0
                } else {
                    TPS_PROXIMITY
                },
            ),
            PK_TIME => put_u32(&mut buf, at, unsafe { GetTickCount() }),
            PK_CHANGED => put_u32(&mut buf, at, pen.changed),
            PK_SERIAL_NUMBER => put_u32(&mut buf, at, pen.serial),
            PK_CURSOR => put_u32(&mut buf, at, PEN_CURSOR_INDEX),
            PK_BUTTONS => put_u32(&mut buf, at, pen.buttons_for_mode(packet_mode)),
            PK_X => put_i32(&mut buf, at, if zero_xy() { 0 } else { packet_x(pen.x, space) }),
            PK_Y => put_i32(&mut buf, at, if zero_xy() { 0 } else { packet_y_space(pen.y, space) }),
            PK_Z => put_i32(&mut buf, at, 0),
            PK_NORMAL_PRESSURE => put_u32(&mut buf, at, pressure),
            PK_TANGENT_PRESSURE => put_u32(&mut buf, at, tangent_pressure),
            PK_ORIENTATION => {
                put_i32(&mut buf, at, azimuth);
                put_i32(&mut buf, at + 4, altitude.min(900));
                put_i32(&mut buf, at + 8, 0);
            }
            PK_ROTATION => {
                put_u32(&mut buf, at, twist as u32);
                put_u32(&mut buf, at + 4, 0);
                put_u32(&mut buf, at + 8, 0);
            }
            _ => {}
        }
    }

    buf
}

/// Drains every pending sample from the shared ring into the Wintab queue.
/// Samples arrive far faster than the consumer polls, so a single "latest"
/// sample per call would silently drop almost all of them.
/// Consumes every queued sample and appends one Wintab packet per sample.
/// Returns true when at least one packet was produced.
fn poll_shared_all(context: &mut Context, context_handle: usize) -> bool {
    let Some(state) = shared() else {
        context.pen.in_range = false;
        return false;
    };

    let now = unsafe { GetTickCount() };
    let heartbeat = unsafe { ptr::read_volatile(&state.heartbeat_ms) };
    let alive = now.wrapping_sub(heartbeat) < 2500;
    let enabled = unsafe { ptr::read_volatile(&state.enabled) } != 0;
    if !alive || !enabled {
        context.pen.in_range = false;
        return false;
    }

    let index = state.write_index.load(Ordering::Acquire);
    let mut cursor = context.pen.ring_cursor;
    if index == cursor {
        return false;
    }
    // Drop samples the consumer can never catch up with.
    if index.wrapping_sub(cursor) > RING_CAPACITY as u32 {
        cursor = index.wrapping_sub(RING_CAPACITY as u32);
    }

    let mut produced = false;
    while cursor != index {
        let slot = (cursor as usize) % RING_CAPACITY;
        let sample = unsafe { ptr::read_volatile(&state.packets[slot]) };
        cursor = cursor.wrapping_add(1);

        let previous = context.pen.last_packet;
        let was_in_range = previous.map_or(false, |p| p.in_range != 0);
        let in_range = sample.in_range != 0;
        // A sample that repeats the previous state is not a new packet. Wacom
        // hardware simply stops interrupting when nothing moves; queueing one
        // packet per idle sample instead makes every message-driven application
        // repaint continuously, and the brush then lags behind the cursor.
        let same_state = previous.map_or(false, |p| {
            p.x == sample.x
                && p.y == sample.y
                && p.pressure == sample.pressure
                && p.tilt_x == sample.tilt_x
                && p.tilt_y == sample.tilt_y
                && p.rotation == sample.rotation
                && p.buttons == sample.buttons
                && p.cursor == sample.cursor
                && p.in_range == sample.in_range
        });
        context.pen.last_packet = Some(sample);
        context.pen.ring_cursor = cursor;
        if !in_range && !was_in_range {
            // Still out of range, nothing changed and nothing to report. The
            // exit transition itself is reported once, below.
            context.pen.in_range = false;
            continue;
        }
        if in_range && same_state {
            context.pen.in_range = true;
            continue;
        }
        context.pen.serial = next_serial(context.pen.serial, sample.sequence);
        context.pen.x = sample.x;
        context.pen.y = sample.y;
        let raw_pressure = if in_range {
            sample.pressure.min(RAW_PRESSURE_MAX)
        } else {
            0
        };
        context.pen.pressure = apply_feel(context, raw_pressure);
        unsafe { DBG_PRESSURE_LAST = context.pen.pressure; }
        if context.pen.pressure > context.peak_pressure {
            context.peak_pressure = context.pen.pressure;
        }
        context.peak_samples = context.peak_samples.saturating_add(1);
        context.pen.tilt_x = sample.tilt_x;
        context.pen.tilt_y = sample.tilt_y;
        context.pen.rotation = sample.rotation;
        // Older OTD plugins publish pkButtons as a Wintab transition code
        // (HIWORD = TBN_DOWN/TBN_UP, LOWORD = button index); newer ones publish
        // the held-state bitmask. Latch transitions into a mask so the tip bit
        // stays set for the whole stroke.
        let prev_latched = context.pen.latched_buttons;
        context.pen.latch_buttons(sample.buttons, in_range);
        context.pen.in_range = in_range;

        // A CXO_SYSTEM context owns the system mouse, so mirroring the latched
        // buttons into real mouse events looks like the right thing to do — but
        // OTD's own platform pointer already injects the click, and doing both
        // turns every tap into a double click. Keep the hook, leave it off.
        if INJECT_SYSTEM_BUTTONS && context.options & CXO_SYSTEM != 0 {
            let before = context.system_buttons;
            let after = context.pen.latched_buttons;
            if before != after {
                inject_system_buttons(before, after);
                context.system_buttons = after;
            }
        }

        let mut changed = PK_X | PK_Y | PK_NORMAL_PRESSURE | PK_ORIENTATION | PK_ROTATION;
        // Compare the latched mask: a held tip produces no new transition sample,
        // so comparing the raw value would report "changed" on every packet.
        if prev_latched != context.pen.latched_buttons {
            changed |= PK_BUTTONS;
        }
        if previous.map_or(true, |p| p.cursor != sample.cursor) {
            changed |= PK_CURSOR;
            context.csr_cursor = sample.cursor;
            context.csr_change_pending = true;
        }
        if previous.map_or(true, |p| p.in_range != sample.in_range) {
            changed |= PK_STATUS;
        }
        context.pen.changed = changed;

        if experiment("silent") {
            continue;
        }
        let mut bytes = serialize(context.packet_data, &context.pen, context_handle, context.packet_mode, PacketSpace { screen_px: screen_space(), sys_ext: context.sys_ext, sys_org: context.sys_org });
        // Experiment: an application that reports no pressure may be reading it
        // from a different slot than the declared layout. Duplicate the value
        // into the following 4-byte slots so a mismatch becomes visible.
        if experiment("spreadpressure") {
            let (_, slots) = packet_layout(context.packet_data);
            if let Some((_, at)) = slots.iter().find(|(bit, _)| *bit == PK_NORMAL_PRESSURE) {
                let value = packet_pressure(context.packet_data, &bytes).to_ne_bytes();
                for extra in 1..=3usize {
                    let pos = at + extra * 4;
                    if pos + 4 <= bytes.len() {
                        bytes[pos..pos + 4].copy_from_slice(&value);
                    }
                }
            }
        }
        let packed_pressure = packet_pressure(context.packet_data, &bytes);
        if packed_pressure > context.peak_packed_pressure {
            context.peak_packed_pressure = packed_pressure;
        }
        let width = bytes.len();
        let serial = context.pen.serial;
        // One line per second: enough to tell whether pressure really reaches
        // the packet, without flooding the file at 200 packets/s. The hex dump
        // now covers the whole packet, including pkNormalPressure for the masks
        // apps actually use (`np` is the serialized value; `raw` is OTD's
        // original 0..32767 reading).
        if serial % 200 == 0 {
            diag(&format!(
                "PKT serial={} mask=0x{:X} len={} x={} y={} raw={} np={} in_range={} status={} hex={}",
                serial,
                context.packet_data,
                width,
                context.pen.x,
                context.pen.y,
                context.pen.pressure,
                packed_pressure,
                context.pen.in_range,
                if context.pen.in_range { 0 } else { TPS_PROXIMITY },
                hexline(&bytes)
            ));
        }
        // Never build a backlog. If the application has not taken the packets
        // already sitting in the queue, they are stale the moment a newer sample
        // arrives: replace them instead of appending. The application then always
        // draws from the current pen state, which is how a real driver behaves,
        // and a window switch can no longer hand Photoshop a stroke's worth of
        // ancient positions to replay.
        if context.queue.len() > context.read {
            context.queue.truncate(context.read);
        }
        context.queue.push(QueuedPacket { serial, bytes });
        context.produced_serial = serial;
        context.last_sample_ms = now;
        produced = true;
    }

    context.pen.last_reported_in_range = context.pen.in_range;
    unsafe { DBG_PRODUCED += if produced { 1 } else { 0 }; }
    produced
}

pub(crate) static mut DBG_PRODUCED: u64 = 0;
pub(crate) static mut DBG_PRESSURE_LAST: u32 = 0;

/// One diagnostic line per second carrying the highest pressure in that window,
/// so a single quick stroke is visible even though the PKT dump only lands on
/// one serial out of every two hundred.
fn flush_peak(context: &mut Context) {
    if context.peak_samples == 0 {
        context.peak_window_start = unsafe { GetTickCount() };
        return;
    }
    let now = unsafe { GetTickCount() };
    if now.wrapping_sub(context.peak_window_start) < 1000 {
        return;
    }
    diag(&format!(
        "PEAK mask=0x{:X} max={} packed={} samples={} last={} msgs={} packets={} misses={} gets={} supp={} q={}/{}",
        context.packet_data,
        context.peak_pressure,
        context.peak_packed_pressure,
        context.peak_samples,
        context.pen.pressure,
        MESSAGES_POSTED.swap(0, Ordering::Relaxed),
        PACKET_CALLS.swap(0, Ordering::Relaxed),
        PACKET_MISSES.swap(0, Ordering::Relaxed),
        context.get_calls,
        context.suppressed_posts,
        context.queue.len().saturating_sub(context.read),
        context.queue.len()
    ));
    context.peak_pressure = 0;
    context.peak_packed_pressure = 0;
    context.peak_samples = 0;
    context.get_calls = 0;
    context.suppressed_posts = 0;
    context.peak_window_start = now;
}

/// Counters printed with the once-per-second PEAK line. The earlier diagnostic
/// counted only the log lines it printed, so "messages posted" could never
/// exceed the print cap and a real flood stayed invisible.
static MESSAGES_POSTED: AtomicU32 = AtomicU32::new(0);
static PACKET_CALLS: AtomicU32 = AtomicU32::new(0);
static PACKET_MISSES: AtomicU32 = AtomicU32::new(0);

fn refresh(context: &mut Context, context_handle: usize) -> bool {
    if !context.enabled {
        return false;
    }

    let produced = poll_shared_all(context, context_handle);
    flush_peak(context);

    // Proximity timeout: a pen that stopped reporting has left the tablet as far
    // as the application is concerned. Emitting the exit here is what lets a
    // window that was switched to the background come back to a clean state
    // instead of an armed stroke with no tool change.
    if !produced && context.pen.in_range && context.last_sample_ms != 0 {
        let now = unsafe { GetTickCount() };
        let silence = now.wrapping_sub(context.last_sample_ms);
        let limit = if context.pen.latched_buttons != 0 {
            PROXIMITY_TIMEOUT_PRESSED_MS
        } else {
            PROXIMITY_TIMEOUT_MS
        };
        if silence > limit {
            context.pen.in_range = false;
            context.pen.pressure = 0;
            context.pen.latched_buttons = 0;
            context.pen.changed = PK_STATUS | PK_BUTTONS | PK_NORMAL_PRESSURE;
        }
    }

    if !produced && context.pen.in_range != context.pen.last_reported_in_range {
        // Report the pen leaving range even when no new samples arrived.
        context.pen.last_reported_in_range = context.pen.in_range;
        context.pen.serial = context.pen.serial.wrapping_add(1);
        let packet = serialize(context.packet_data, &context.pen, context_handle, context.packet_mode, PacketSpace { screen_px: screen_space(), sys_ext: context.sys_ext, sys_org: context.sys_org });
        context.queue.push(QueuedPacket {
            serial: context.pen.serial,
            bytes: packet,
        });
        context.produced_serial = context.pen.serial;
    }

    // Bound the history by total length, not by the read cursor: WTPacket never
    // advances the cursor (a serial is looked up more than once), so a
    // pending-based trim could never fire and the queue would grow without
    // limit. Anything older than MAX_QUEUE packets is stale for painting anyway.
    let overflow = context.queue.len().saturating_sub(MAX_QUEUE);
    if overflow > 0 {
        context.queue.drain(0..overflow);
        context.read = context.read.saturating_sub(overflow);
    }

    produced || context.queue.len() > context.read
}

// ---------------------------------------------------------------- WTInfo

fn write_bytes(dst: *mut c_void, bytes: &[u8]) -> u32 {
    if !dst.is_null() {
        unsafe { ptr::copy_nonoverlapping(bytes.as_ptr(), dst as *mut u8, bytes.len()) };
    }
    bytes.len() as u32
}

fn write_u32(dst: *mut c_void, value: u32) -> u32 {
    if !dst.is_null() {
        unsafe { ptr::write_unaligned(dst as *mut u32, value) };
    }
    4
}

fn write_u16(dst: *mut c_void, value: u16) -> u32 {
    if !dst.is_null() {
        unsafe { ptr::write_unaligned(dst as *mut u16, value) };
    }
    2
}

/// CSR_BUTTONS and CSR_NPBUTTON are BYTE-sized per the Wintab reference.
fn write_u8(dst: *mut c_void, value: u8) -> u32 {
    if !dst.is_null() {
        unsafe { ptr::write_unaligned(dst as *mut u8, value) };
    }
    1
}

fn write_i32(dst: *mut c_void, value: i32) -> u32 {
    if !dst.is_null() {
        unsafe { ptr::write_unaligned(dst as *mut i32, value) };
    }
    4
}

/// Builds an AXIS in centimetres, matching the values Wacom's driver reports
/// for the tablet's X/Y/Z axes (units = TU_CM, resolution = 2000 lines/cm).
fn write_axis_cm(dst: *mut c_void, min: i32, max: i32) -> u32 {
    let axis = Axis {
        min,
        max,
        units: TU_CM,
        resolution: 2000u32 << 16,
    };
    let bytes = unsafe {
        std::slice::from_raw_parts(&axis as *const Axis as *const u8, std::mem::size_of::<Axis>())
    };
    write_bytes(dst, bytes)
}

/// Builds an AXIS. `resolution` is FIX32 (16.16 fixed point), so callers pass
/// units-per-millimetre and it is converted here.
fn write_axis(dst: *mut c_void, min: i32, max: i32, resolution_per_mm: u32) -> u32 {
    let axis = Axis {
        min,
        max,
        units: TU_NONE,
        resolution: resolution_per_mm.saturating_mul(65536),
    };
    let bytes = unsafe {
        std::slice::from_raw_parts(&axis as *const Axis as *const u8, std::mem::size_of::<Axis>())
    };
    write_bytes(dst, bytes)
}

/// CSR_NPRESPONSE / CSR_TPRESPONSE are 256-entry response curves that map the
/// raw pressure range onto logical pressure. Returning a single zero told
/// Photoshop and SAI that every pressure value maps to zero, which is why the
/// packets carried 21770 while the brush stayed flat. This table is the
/// identity curve over 0..MAX_PRESSURE.
fn write_identity_pressure_response(dst: *mut c_void) -> u32 {
    let mut curve = [0u8; 256 * 4];
    for i in 0..256u32 {
        let value = (i * MAX_PRESSURE as u32) / 255;
        let at = i as usize * 4;
        curve[at..at + 4].copy_from_slice(&value.to_le_bytes());
    }
    write_bytes(dst, &curve)
}

fn write_axes3(dst: *mut c_void, axes: [(i32, i32); 3], resolution_per_circle: u32) -> u32 {
    let mut out = Vec::with_capacity(3 * std::mem::size_of::<Axis>());
    for (min, max) in axes {
        let axis = Axis {
            min,
            max,
            units: TU_CIRCLE,
            // Orientation/rotation data is in tenths of a degree, so one full
            // revolution is 3600 increments. Krita, Blender and Photoshop all
            // gate tilt support on this being non-zero; leaving it at 0 made
            // every one of them report "no tilt" even though the packets carried
            // azimuth and altitude.
            resolution: resolution_per_circle << 16,
        };
        let bytes = unsafe {
            std::slice::from_raw_parts(&axis as *const Axis as *const u8, std::mem::size_of::<Axis>())
        };
        out.extend_from_slice(bytes);
    }
    write_bytes(dst, &out)
}

/// Writes three UINT values (button maps are arrays, not scalars).
fn write_array3(dst: *mut c_void, values: [u32; 3]) -> u32 {
    let mut out = Vec::with_capacity(12);
    for value in values {
        out.extend_from_slice(&value.to_le_bytes());
    }
    write_bytes(dst, &out)
}

fn write_text(dst: *mut c_void, text: &str) -> u32 {
    let mut units: Vec<u16> = text.encode_utf16().collect();
    units.push(0);
    let bytes = unsafe { std::slice::from_raw_parts(units.as_ptr() as *const u8, units.len() * 2) };
    write_bytes(dst, bytes)
}

/// Narrow (char-width) string writer for the WTI_* A entry points.
fn write_narrow(dst: *mut c_void, text: &str) -> u32 {
    let mut bytes: Vec<u8> = text.as_bytes().to_vec();
    bytes.push(0);
    write_bytes(dst, &bytes)
}

/// One physical cursor/Wintab cursor slot. Wacom's PTH-660 driver declares six:
/// a puck, the "Pressure Stylus" pen (the one Photoshop and SAI index for
/// pressure), the eraser, a second puck, and two further slots. Applications
/// resolve pressure through the cursor's own CSR items, so the pen has to sit
/// at the index the hardware driver uses (1) and NCURSORS has to cover it.
struct CursorSpec {
    name: &'static str,
    cursor_type: u32,
    pktdata: u32,
    buttons: u8,
    button_bits: u8,
    button_names: &'static str,
    sys_button_map: [u8; 4],
    phys_id: u32,
    mode: Option<u32>,
    capabilities: Option<u32>,
    pressure_items: bool,
}

const CURSOR_COUNT: u32 = 6;

/// Slot the pen lives in. Matches Wacom's own driver: the "Pressure Stylus"
/// cursor is index 1, so packets report PK_CURSOR = 1.
const PEN_CURSOR_INDEX: u32 = 1;

const PUCK_BUTTON_NAMES: &str = "button 1\0button 2\0button 3\0button 4\0button 5\0\
button 6\0button 7\0button 8\0button 9\0button 10\0button 11\0button 12\0\
button 13\0button 14\0button 15\0button 16\0";

const CURSORS: [CursorSpec; CURSOR_COUNT as usize] = [
    CursorSpec {
        name: "Puck",
        cursor_type: 0x8000,
        pktdata: 0x0000_13FF,
        buttons: 16,
        button_bits: 5,
        button_names: PUCK_BUTTON_NAMES,
        sys_button_map: [2, 1, 4, 7],
        phys_id: 0,
        mode: None,
        capabilities: Some(0),
        pressure_items: false,
    },
    CursorSpec {
        name: "Pressure Stylus",
        cursor_type: 0x4842,
        pktdata: 0x0003_17FF,
        buttons: 3,
        button_bits: 4,
        button_names: "tip\0barrel\0barrel2\0",
        sys_button_map: [1, 4, 2, 0],
        phys_id: 0x6B80_8549,
        mode: Some(0),
        capabilities: Some(1),
        pressure_items: true,
    },
    CursorSpec {
        name: "Eraser  ",
        cursor_type: 0x4842,
        pktdata: 0x0003_17FF,
        buttons: 1,
        button_bits: 4,
        button_names: "eraser",
        sys_button_map: [1, 0, 0, 0],
        phys_id: 0x6B80_8549,
        mode: Some(1),
        capabilities: Some(5),
        pressure_items: true,
    },
    CursorSpec {
        name: "Puck",
        cursor_type: 0x8000,
        pktdata: 0x0000_13FF,
        buttons: 16,
        button_bits: 5,
        button_names: PUCK_BUTTON_NAMES,
        sys_button_map: [2, 1, 4, 7],
        phys_id: 0,
        mode: None,
        capabilities: Some(0),
        pressure_items: false,
    },
    CursorSpec {
        name: "Puck",
        cursor_type: 0x4000,
        pktdata: 0x0000_1DFF,
        buttons: 8,
        button_bits: 4,
        button_names: PUCK_BUTTON_NAMES,
        sys_button_map: [1, 0, 0, 0],
        phys_id: 0x6B80_8549,
        mode: None,
        capabilities: Some(0),
        pressure_items: false,
    },
    CursorSpec {
        name: "Puck",
        cursor_type: 0xC000,
        pktdata: 0x0000_15FF,
        buttons: 8,
        button_bits: 4,
        button_names: PUCK_BUTTON_NAMES,
        sys_button_map: [1, 0, 0, 0],
        phys_id: 0,
        mode: None,
        capabilities: Some(0),
        pressure_items: false,
    },
];

/// Cursor index carried by a WTI_CURSORS category, or None if the category is
/// not one of the declared cursor slots.
fn cursor_slot(category: u32) -> Option<u32> {
    if category >= WTI_CURSORS && category < WTI_CURSORS + CURSOR_COUNT {
        Some(category - WTI_CURSORS)
    } else {
        None
    }
}

fn cursor_item(slot: u32, item: u32, output: *mut c_void, wide: bool) -> u32 {
    let spec = &CURSORS[slot as usize];
    match item {
        CSR_NAME => {
            if wide {
                write_text(output, spec.name)
            } else {
                write_narrow(output, spec.name)
            }
        }
        CSR_ACTIVE => write_u32(output, 1),
        CSR_PKTDATA => write_u32(output, spec.pktdata),
        CSR_BUTTONS => write_u8(output, spec.buttons),
        CSR_BUTTONBITS => write_u8(output, spec.button_bits),
        CSR_BTNNAMES => {
            if wide {
                write_text(output, spec.button_names)
            } else {
                write_narrow(output, spec.button_names)
            }
        }
        CSR_BUTTONMAP => {
            let mut map = [0u8; 32];
            for (i, slot) in map.iter_mut().enumerate() {
                *slot = i as u8;
            }
            write_bytes(output, &map)
        }
        CSR_SYSBTNMAP => {
            let mut map = [0u8; 32];
            map[..4].copy_from_slice(&spec.sys_button_map);
            write_bytes(output, &map)
        }
        CSR_NPBUTTON => {
            if spec.pressure_items {
                write_u8(output, 0)
            } else {
                0
            }
        }
        CSR_NPBTNMARKS => {
            if spec.pressure_items {
                write_bytes(output, &[0, 0, 0, 0, 1, 0, 0, 0])
            } else {
                0
            }
        }
        CSR_NPRESPONSE => {
            if spec.pressure_items {
                write_bytes(output, &[0u8; 1024])
            } else {
                0
            }
        }
        CSR_PHYSID => write_u32(output, spec.phys_id),
        CSR_MODE => match spec.mode {
            Some(value) => write_u32(output, value),
            None => 0,
        },
        CSR_CAPABILITIES => match spec.capabilities {
            Some(value) => write_u32(output, value),
            None => 0,
        },
        CSR_TYPE => write_u32(output, spec.cursor_type),
        _ => 0,
    }
}

fn scalar(category: u32, index: u32, output: *mut c_void) -> u32 {
    if let Some(slot) = cursor_slot(category) {
        return cursor_item(slot, index, output, true);
    }
    match (category, index) {
        (WTI_INTERFACE, IFC_SPECVERSION) => {
            // IFC_SPECVERSION is a WORD: high byte major, low byte minor, so
            // Wintab 1.4 is 0x0104. A DWORD here reads as 0.4 for any
            // application that follows the header and stores a WORD.
            write_u16(output, 0x0104)
        }
        (WTI_INTERFACE, IFC_IMPLVERSION) => write_u16(output, 0x0127),
        (WTI_INTERFACE, IFC_NDEVICES) => write_u32(output, 1),
        (WTI_INTERFACE, IFC_NCURSORS) => write_u32(output, CURSOR_COUNT),
        // Applications that open one context per document/tool ask for this
        // before WTOpen; reporting a single slot made multi-context apps refuse
        // the second handle.
        (WTI_INTERFACE, IFC_NCONTEXTS) => write_u32(output, 32),
        // Matches Wacom: CXO_SYSTEM | CXO_MESSAGES | CXO_MARGIN = 0x0D.
        (WTI_INTERFACE, IFC_CTXOPTIONS) => write_u32(output, 0x0000_000D),
        // Wacom reports the LOGCONTEXTA size here (172); apps allocate that
        // much before WTSave. Our LOGCONTEXTW is larger, so a value of 212
        // would overflow a caller that trusts this size.
        (WTI_INTERFACE, IFC_CTXSAVESIZE) => write_u32(output, 172),
        (WTI_INTERFACE, IFC_NEXTENSIONS) => write_u32(output, 0),
        (WTI_INTERFACE, IFC_NMANAGERS) => write_u32(output, 9),
        // PTH-660 is a physical pen tablet with hardware proximity detection and
        // per-cursor physical IDs, not an integrated screen digitizer. Reporting
        // HWC_INTEGRATED (1) made Windows-side tablet code treat it as a
        // touchscreen and skip the pen pressure path.
        (WTI_DEVICES, DVC_HARDWARE) => write_u32(output, 0x0004 | 0x0008),
        (WTI_DEVICES, DVC_NCSRTYPES) => write_u32(output, CURSOR_COUNT),
        // Wintab cursor numbers are zero-based: WTI_CURSORS (200) is cursor 0,
        // the only cursor this device declares. Reporting 1 here made
        // Photoshop look up category 201, where every CSR item returned zero,
        // so it classified the pen as an unknown cursor with no pressure.
        (WTI_DEVICES, DVC_FIRSTCSR) => write_u32(output, 0),
        (WTI_DEVICES, DVC_PKTRATE) => write_u32(output, PKT_RATE),
        // Wacom's own driver splits this the same way: DVC_PKTDATA lists what is
        // always available from the device (context/status/time/changed/serial/
        // cursor/buttons/X/Y = 0x1FF) while pressure, tilt and rotation are per-
        // cursor capabilities reported through DVC_CSRDATA (0x1E00).
        (WTI_DEVICES, DVC_PKTDATA) => write_u32(output, 0x0000_01FF),
        (WTI_DEVICES, DVC_PKTMODE) => write_u32(output, TPS_NONE),
        (WTI_DEVICES, DVC_CSRDATA) => write_u32(output, 0x0000_1E00),
        (WTI_DEVICES, DVC_XMARGIN) => write_i32(output, 0),
        (WTI_DEVICES, DVC_YMARGIN) => write_i32(output, 0),
        (WTI_DEVICES, DVC_ZMARGIN) => write_i32(output, 0),
        // Resolution is FIX32 lines per inch, not a raw count: 100 lpi is the
        // value Wacom's driver advertises, and 100 must be shifted into the
        // high word. The old raw widths read as ~0.002 lpi.
        (WTI_DEVICES, DVC_X) => {
            let (w, _) = device_extent();
            write_axis_cm(output, 0, w - 1)
        }
        (WTI_DEVICES, DVC_Y) => {
            let (_, h) = device_extent();
            write_axis_cm(output, 0, h - 1)
        }
        (WTI_DEVICES, DVC_Z) => write_axis_cm(output, -1023, 1023),
        (WTI_DEVICES, DVC_NPRESSURE) => write_axis(output, 0, MAX_PRESSURE as i32, 0),
        (WTI_DEVICES, DVC_TPRESSURE) => {
            write_axis(output, 0, MAX_TANGENT_PRESSURE as i32, 0)
        }
        // DVC_ORIENTATION and DVC_ROTATION are arrays of three AXIS structures,
        // not a bit field. Reporting a single UINT here made every app that
        // probes for tilt/rotation read four bytes of nonsense.
        (WTI_DEVICES, DVC_ORIENTATION) => {
            write_axes3(output, [(0, 3600), (-900, 900), (0, 3600)], 3600)
        }
        // The Intuos Pro M has no twist sensor, and Wacom's own driver leaves
        // DVC_ROTATION unimplemented for it. Advertising rotation made
        // Photoshop/Krita enable brush-rotation UI that never received data.
        (WTI_DEVICES, DVC_ROTATION) => 0,
        (WTI_CURSORS, CSR_ACTIVE) => write_u32(output, 1),
        // Pressure Stylus cursor: pressure + orientation, no tangential
        // pressure. This is the exact mask Wacom reports for the Intuos Pro
        // pen cursor (0x317FF), and it is what Photoshop probes before it
        // decides whether the cursor can report pressure at all.
        (WTI_CURSORS, CSR_PKTDATA) => write_u32(output, 0x0003_17FF),
        (WTI_CURSORS, CSR_TYPE) => write_u32(output, CSRPEN),
        // CSR_BUTTONS (item 4) is the BYTE button count and CSR_BUTTONBITS
        // (item 5) is the BYTE bit count. Wacom's "Pressure Stylus" reports
        // 3 buttons over 4 bits; the previous 4/3 pair had the two item
        // values swapped, which made Photoshop build the wrong click mask.
        (WTI_CURSORS, CSR_BUTTONS) => write_u8(output, 3),
        (WTI_CURSORS, CSR_BUTTONBITS) => write_u8(output, 4),
        // WTInfoW returns wide strings: writing "Tip\0Side\0..." as bytes made
        // the button names read back as garbage.
        (WTI_CURSORS, CSR_BTNNAMES) => write_text(output, "tip\0barrel\0barrel2"),
        // Logical button map is the identity (0..31).
        (WTI_CURSORS, CSR_BUTTONMAP) => {
            let mut map = [0u8; 32];
            for (i, slot) in map.iter_mut().enumerate() {
                *slot = i as u8;
            }
            write_bytes(output, &map)
        }
        // System button map: logical button 0 (tip) -> SBN_LCLICK (1),
        // 1 -> SBN_RCLICK (4), 2 -> SBN_LDBLCLICK (2). Wacom's values are
        // 01 04 02 00; the old 00 01 02 03 map sent the tip to SBN_NONE, so
        // apps translating through it never saw a normal click.
        (WTI_CURSORS, CSR_SYSBTNMAP) => {
            let mut map = [0u8; 32];
            map[0] = 1;
            map[1] = 4;
            map[2] = 2;
            write_bytes(output, &map)
        }
        // Wacom DOES implement the pressure-button items for the pen: the tip
        // is logical button 0, marks are {0, 1}, and the 256-entry response
        // table is all zeros (= linear). Photoshop queries all three while it
        // initialises its tablet path, so answering "unsupported" made it
        // classify the pen as non-pressure-capable.
        (WTI_CURSORS, CSR_NPBUTTON) => write_u8(output, 0),
        (WTI_CURSORS, CSR_NPBTNMARKS) => {
            let marks = [0u32, 1u32];
            let mut bytes = Vec::with_capacity(8);
            for mark in marks {
                bytes.extend_from_slice(&mark.to_le_bytes());
            }
            write_bytes(output, &bytes)
        }
        (WTI_CURSORS, CSR_NPRESPONSE) => write_bytes(output, &[0u8; 1024]),
        (WTI_CURSORS, CSR_TPBUTTON) => 0,
        (WTI_CURSORS, CSR_TPBTNMARKS) => 0,
        (WTI_CURSORS, CSR_TPRESPONSE) => 0,
        (WTI_CURSORS, CSR_PHYSID) => write_u32(output, 0x6B80_8549),
        (WTI_CURSORS, CSR_MODE) => write_u32(output, 0),
        // Wacom leaves these unimplemented; apps probe the size and skip them.
        (WTI_CURSORS, CSR_MINPKTDATA) => 0,
        (WTI_CURSORS, CSR_MINBUTTONS) => 0,
        (WTI_CURSORS, CSR_CAPABILITIES) => write_u32(output, 1),
        (WTI_STATUS, STA_CONTEXTS) => write_u32(output, 1),
        (WTI_STATUS, STA_SYSCTXS) => write_u32(output, 0),
        (WTI_STATUS, STA_PKTRATE) => write_u32(output, PKT_RATE),
        (WTI_STATUS, STA_PKTDATA) => write_u32(output, 0x0000_01FF),
        (WTI_STATUS, STA_MANAGERS) => write_u32(output, 1),
        (WTI_STATUS, STA_SYSTEM) => write_u32(output, 0),
        (WTI_STATUS, STA_BUTTONUSE) => write_u32(output, 0),
        (WTI_STATUS, STA_SYSBTNUSE) => write_u32(output, 0),
        (WTI_STATUS, _) => write_u32(output, 1),
        (WTI_DEFCONTEXT, _) | (WTI_DEFSYSCTX, _) | (WTI_DDCTXS, _) | (WTI_DSCTXS, _) => {
            // Defaults are filled by the A/W variants: LOGCONTEXTW has a 40-character
            // (80-byte) name, LOGCONTEXTA a 40-byte one, so returning the A struct to
            // WTInfoW shifts every field by 40 bytes.
            0
        }
        _ => 0,
    }
}

fn write_context_a(output: *mut c_void) -> u32 {
    let ctx = base_context_a();
    let bytes = unsafe {
        std::slice::from_raw_parts(
            &ctx as *const LogContextA as *const u8,
            std::mem::size_of::<LogContextA>(),
        )
    };
    write_bytes(output, bytes)
}

fn write_context_w(output: *mut c_void) -> u32 {
    let ctx = base_context_w();
    let bytes = unsafe {
        std::slice::from_raw_parts(
            &ctx as *const LogContextW as *const u8,
            std::mem::size_of::<LogContextW>(),
        )
    };
    write_bytes(output, bytes)
}

/// Largest single category we can return: the full W-layout LOGCONTEXT from
/// WTI_DEFCONTEXT / WTI_DEFSYSCTX. `WTInfo(0, 0, NULL)` must report this so
/// applications can tell the driver is present.
fn largest_category_size() -> u32 {
    std::mem::size_of::<LogContextW>() as u32
}

/// WTI_DEFCONTEXT and friends carry a LOGCONTEXT of the call's char width.
fn is_context_category(category: u32) -> bool {
    matches!(
        category,
        WTI_DEFCONTEXT | WTI_DEFSYSCTX | WTI_DDCTXS | WTI_DSCTXS
    )
}

// ---------------------------------------------------------------- exports

#[no_mangle]
pub unsafe extern "system" fn WTInfoA(category: u32, index: u32, output: *mut c_void) -> u32 {
    trace(&format!(
        "WTInfoA cat={} idx={} out_null={}",
        category, index, output.is_null()
    ));
    if category == 0 {
        // Spec: category zero copies no data but reports the size of the
        // largest complete category. Applications use this as the "is a
        // tablet driver alive?" probe; returning zero reads as "no tablet".
        diag(&format!(
            "WTInfoA probe cat=0 idx={} -> size={}",
            index,
            largest_category_size()
        ));
        return largest_category_size();
    }
    if is_context_category(category) {
        diag(&format!("WTInfoA context cat={} index={}", category, index));
        return write_context_a(output);
    }
    if let Some(slot) = cursor_slot(category) {
        return cursor_item(slot, index, output, false);
    }
    match (category, index) {
        (WTI_INTERFACE, IFC_WINTABID) => write_bytes(output, b"OpenTabletDriver Wintab 1.0\0"),
        (WTI_DEVICES, DVC_NAME) => write_bytes(output, b"WACOM Tablet\0"),
        (WTI_DEVICES, DVC_PNPID) => write_bytes(output, b"6LQ00A1034520\0"),
        (WTI_CURSORS, CSR_NAME) => write_bytes(output, b"Pressure Stylus\0"),
        _ => scalar(category, index, output),
    }
}

#[no_mangle]
pub unsafe extern "system" fn WTInfoW(category: u32, index: u32, output: *mut c_void) -> u32 {
    trace(&format!(
        "WTInfoW cat={} idx={} out_null={}",
        category, index, output.is_null()
    ));
    if category == 0 {
        // Photoshop calls this before every poll; keep only the first few in
        // the log so a long session does not turn into megabytes of probes.
        static PROBES: AtomicU32 = AtomicU32::new(0);
        if PROBES.fetch_add(1, Ordering::Relaxed) < 20 {
            diag(&format!(
                "WTInfoW probe cat=0 idx={} -> size={}",
                index,
                largest_category_size()
            ));
        }
        return largest_category_size();
    }
    if is_context_category(category) {
        diag(&format!("WTInfoW context cat={} index={}", category, index));
        return write_context_w(output);
    }
    if let Some(slot) = cursor_slot(category) {
        return cursor_item(slot, index, output, true);
    }
    if category == WTI_DEVICES
        && (index == DVC_NPRESSURE || index == DVC_X || index == DVC_Y)
        && !output.is_null()
    {
        diag(&format!("WTInfoW DVC idx={} -> axis fill", index));
    }
    match (category, index) {
        (WTI_INTERFACE, IFC_WINTABID) => write_text(output, "OpenTabletDriver Wintab 1.0"),
        (WTI_DEVICES, DVC_NAME) => write_text(output, "WACOM Tablet"),
        (WTI_DEVICES, DVC_PNPID) => write_text(output, "6LQ00A1034520"),
        (WTI_CURSORS, CSR_NAME) => write_text(output, "Pressure Stylus"),
        _ => scalar(category, index, output),
    }
}

#[no_mangle]
pub unsafe extern "system" fn WTOpenA(
    hwnd: HWND,
    input: *const LogContextA,
    enable: i32,
) -> *mut c_void {
    refresh_display();
    let ctx = if input.is_null() {
        base_context_a()
    } else {
        unsafe { ptr::read_unaligned(input) }
    };
    open_context(
        ctx.pkt_data,
        ctx.pkt_mode,
        enable != 0,
        hwnd as usize,
        ctx.msg_base,
        ctx.options,
        [ctx.sys_org[0], ctx.sys_org[1]],
        [ctx.sys_ext[0], ctx.sys_ext[1]],
    )
}

#[no_mangle]
pub unsafe extern "system" fn WTOpenW(
    hwnd: HWND,
    input: *const LogContextW,
    enable: i32,
) -> *mut c_void {
    refresh_display();
    let ctx = if input.is_null() {
        base_context_w()
    } else {
        unsafe { ptr::read_unaligned(input) }
    };
    open_context(
        ctx.pkt_data,
        ctx.pkt_mode,
        enable != 0,
        hwnd as usize,
        ctx.msg_base,
        ctx.options,
        [ctx.sys_org[0], ctx.sys_org[1]],
        [ctx.sys_ext[0], ctx.sys_ext[1]],
    )
}

#[no_mangle]
pub unsafe extern "system" fn WTClose(handle: *mut c_void) -> i32 {
    trace(&format!("WTClose h=0x{:X}", handle as usize));
    let index = handle as usize;
    let mut reg = registry().lock().unwrap_or_else(|e| e.into_inner());
    if index == 0 || index >= reg.slots.len() || reg.slots[index].is_none() {
        return 0;
    }
    if let Some(ctx) = &reg.slots[index] {
        if ctx.options & CXO_SYSTEM != 0 && ctx.system_buttons != 0 {
            inject_system_buttons(ctx.system_buttons, 0);
        }
    }
    reg.slots[index] = None;
    1
}

#[no_mangle]
pub unsafe extern "system" fn WTEnable(handle: *mut c_void, enable: i32) -> i32 {
    trace(&format!("WTEnable h=0x{:X} enable={}", handle as usize, enable));
    with_context(handle, |ctx| ctx.enabled = enable != 0).map_or(0, |_| 1)
}

#[no_mangle]
pub unsafe extern "system" fn WTOverlap(handle: *mut c_void, top: i32) -> i32 {
    trace(&format!("WTOverlap h=0x{:X} top={}", handle as usize, top));
    with_context(handle, |_| ()).map_or(0, |_| 1)
}

#[no_mangle]
pub unsafe extern "system" fn WTConfig(_handle: *mut c_void, _hwnd: HWND) -> i32 {
    0
}

#[no_mangle]
pub unsafe extern "system" fn WTSave(_handle: *mut c_void, _output: *mut c_void) -> i32 {
    0
}

#[no_mangle]
pub unsafe extern "system" fn WTRestore(
    _hwnd: HWND,
    _input: *mut c_void,
    enable: i32,
) -> *mut c_void {
    open_context(
        default_mask(),
        TPS_NONE,
        enable != 0,
        _hwnd as usize,
        0x7FF0,
        CXO_SYSTEM,
        screen_origin(),
        screen_extent(),
    )
}

#[no_mangle]
pub unsafe extern "system" fn WTExtGet(_handle: *mut c_void, _tag: u32, _data: *mut c_void) -> i32 {
    0
}

#[no_mangle]
pub unsafe extern "system" fn WTExtSet(_handle: *mut c_void, _tag: u32, _data: *mut c_void) -> i32 {
    0
}

#[no_mangle]
pub unsafe extern "system" fn WTQueueSizeSet(handle: *mut c_void, size: i32) -> i32 {
    trace(&format!("WTQueueSizeSet h=0x{:X} size={}", handle as usize, size));
    with_context(handle, |ctx| {
        ctx.queue.reserve(size.clamp(1, 4096) as usize);
    })
    .map_or(0, |_| 1)
}

#[no_mangle]
pub unsafe extern "system" fn WTQueueSizeGet(handle: *mut c_void) -> i32 {
    trace(&format!("WTQueueSizeGet h=0x{:X}", handle as usize));
    with_context(handle, |ctx| 64.max(ctx.queue.capacity() as i32)).unwrap_or(0)
}

#[no_mangle]
pub unsafe extern "system" fn WTGetA(handle: *mut c_void, output: *mut LogContextA) -> i32 {
    if output.is_null() {
        return 0;
    }
    with_context(handle, |ctx| {
        let mut value = base_context_a();
        value.pkt_data = ctx.packet_data;
        value.pkt_mode = ctx.packet_mode;
        unsafe { ptr::write_unaligned(output, value) };
    })
    .map_or(0, |_| 1)
}

#[no_mangle]
pub unsafe extern "system" fn WTGetW(handle: *mut c_void, output: *mut LogContextW) -> i32 {
    if output.is_null() {
        return 0;
    }
    with_context(handle, |ctx| {
        let mut value = base_context_w();
        value.pkt_data = ctx.packet_data;
        value.pkt_mode = ctx.packet_mode;
        unsafe { ptr::write_unaligned(output, value) };
    })
    .map_or(0, |_| 1)
}

#[no_mangle]
pub unsafe extern "system" fn WTSetA(handle: *mut c_void, input: *const LogContextA) -> i32 {
    if input.is_null() {
        return 0;
    }
    let value = unsafe { ptr::read_unaligned(input) };
    with_context(handle, |ctx| {
        if value.pkt_data != 0 {
            ctx.packet_data = value.pkt_data;
        }
        ctx.packet_mode = value.pkt_mode;
    })
    .map_or(0, |_| 1)
}

#[no_mangle]
pub unsafe extern "system" fn WTSetW(handle: *mut c_void, input: *const LogContextW) -> i32 {
    if input.is_null() {
        trace("WTSetW null");
        return 0;
    }
    let value = unsafe { ptr::read_unaligned(input) };
    trace(&format!(
        "WTSetW h=0x{:X} pkt_data=0x{:X} pkt_mode=0x{:X}",
        handle as usize, value.pkt_data, value.pkt_mode
    ));
    with_context(handle, |ctx| {
        if value.pkt_data != 0 {
            ctx.packet_data = value.pkt_data;
        }
        ctx.packet_mode = value.pkt_mode;
    })
    .map_or(0, |_| 1)
}

#[no_mangle]
pub unsafe extern "system" fn WTPacket(
    handle: *mut c_void,
    serial: u32,
    output: *mut c_void,
) -> i32 {
    if output.is_null() {
        trace("WTPacket output=null");
        return 0;
    }
    trace(&format!(
        "WTPacket h=0x{:X} serial={}",
        handle as usize, serial
    ));
    PACKET_CALLS.fetch_add(1, Ordering::Relaxed);
    with_context(handle, |ctx| {
        ctx.last_consume_ms = unsafe { GetTickCount() };
        // everything up to it. Returning "the pen's current state" instead
        // desynchronised apps that walk the queue by serial (Photoshop, SAI)
        // and discarded every sample in between.
        // The queue is ordered by serial, so a lookup is a binary search. Poll
        // the shared ring only when the requested serial is not present yet:
        // an app walking a burst of serials otherwise pays a full ring poll
        // (and a queue trim) for every one of them.
        let mut index = ctx
            .queue
            .partition_point(|p| p.serial < serial);
        if index >= ctx.queue.len() || ctx.queue[index].serial != serial {
            poll_shared_all(ctx, handle as usize);
            index = ctx.queue.partition_point(|p| p.serial < serial);
        }
        let found = index < ctx.queue.len() && ctx.queue[index].serial == serial;
        if !found {
            PACKET_MISSES.fetch_add(1, Ordering::Relaxed);
            static MISS: AtomicU32 = AtomicU32::new(0);
            let seen = MISS.fetch_add(1, Ordering::Relaxed);
            if seen < 20 || seen % 500 == 0 {
                diag(&format!(
                    "MISS serial={} head={:?} tail={:?} queued={}",
                    serial,
                    ctx.queue.get(ctx.read).map(|p| p.serial),
                    ctx.queue.last().map(|p| p.serial),
                    ctx.queue.len()
                ));
            }
            return 0;
        }
        let packet = ctx.queue[index].bytes.clone();
        unsafe {
            ptr::copy_nonoverlapping(packet.as_ptr(), output as *mut u8, packet.len());
        }
        static PACKS: AtomicU32 = AtomicU32::new(0);
        const PACK_LOG: u32 = 30;
        const PACK_NZ_LOG: u32 = 120;
        let seen = PACKS.fetch_add(1, Ordering::Relaxed);
        let np = packet_pressure(ctx.packet_data, &packet);
        let btn = packet_field(ctx.packet_data, &packet, PK_BUTTONS);
        let log_this = if seen < PACK_LOG {
            true
        } else if np != 0 {
            static PACKS_NZ: AtomicU32 = AtomicU32::new(0);
            PACKS_NZ.fetch_add(1, Ordering::Relaxed) < PACK_NZ_LOG
        } else {
            false
        };
        if log_this {
            diag(&format!(
                "PACK serial={} len={} mask=0x{:X} np={} btn=0x{:X} hex={}",
                serial,
                packet.len(),
                ctx.packet_data,
                np,
                btn,
                hexline(&packet)
            ));
        }
        // Keep the packet in the queue. Wintab applications look a serial up
        // more than once - once while handling WT_PACKET, again while painting
        // - and discarding everything up to it turned the second lookup into a
        // MISS, which leaves the caller holding a stale (usually zero) pressure.
        // History is bounded by MAX_QUEUE in refresh().
        1
    })
    .unwrap_or(0)
}

#[no_mangle]
pub unsafe extern "system" fn WTPacketsGet(
    handle: *mut c_void,
    maximum: i32,
    output: *mut c_void,
) -> i32 {
    trace(&format!(
        "WTPacketsGet h=0x{:X} max={} out_null={}",
        handle as usize, maximum, output.is_null()
    ));
    if maximum <= 0 || output.is_null() {
        return 0;
    }
    with_context(handle, |ctx| {
        refresh(ctx, handle as usize);
        ctx.get_calls = ctx.get_calls.saturating_add(1);
        ctx.last_consume_ms = unsafe { GetTickCount() };
        let count = ctx.queue.len().saturating_sub(ctx.read).min(maximum as usize);
        let width = packet_layout(ctx.packet_data).0;
        {
            static CALLS: AtomicU32 = AtomicU32::new(0);
            let seen = CALLS.fetch_add(1, Ordering::Relaxed);
            if count == 0 && seen < 20 {
                // An application that keeps getting zero packets looks exactly
                // like one that never asks, so record the empty calls too.
                diag(&format!(
                    "GET0 max={} queued={} read={} mask=0x{:X}",
                    maximum, ctx.queue.len(), ctx.read, ctx.packet_data
                ));
            }
        }
        if count > 0 {
            static GETS: AtomicU32 = AtomicU32::new(0);
            let seen = GETS.fetch_add(1, Ordering::Relaxed);
            if seen < 12 {
                // Report the pressure actually present in the packet we are
                // handing over, so a zero on the application side can be told
                // apart from a zero on ours.
                let np_at = packet_layout(ctx.packet_data)
                    .1
                    .iter()
                    .find(|(bit, _)| *bit == PK_NORMAL_PRESSURE)
                    .map(|(_, at)| *at);
                let np = np_at
                    .and_then(|at| ctx.queue[ctx.read].bytes.get(at..at + 4))
                    .map(|s| u32::from_le_bytes([s[0], s[1], s[2], s[3]]))
                    .unwrap_or(0);
                diag(&format!(
                    "GET count={} queued={} mask=0x{:X} width={} np={} cursor={} btn=0x{:X}",
                    count,
                    ctx.queue.len(),
                    ctx.packet_data,
                    width,
                    np,
                    packet_field(ctx.packet_data, &ctx.queue[ctx.read].bytes, PK_CURSOR),
                    packet_field(ctx.packet_data, &ctx.queue[ctx.read].bytes, PK_BUTTONS)
                ));
            }
        }
        let mut write = output as *mut u8;
        for _ in 0..count {
            let packet = &ctx.queue[ctx.read].bytes;
            unsafe {
                ptr::copy_nonoverlapping(packet.as_ptr(), write, packet.len().min(width));
                write = write.add(width);
            }
            ctx.read += 1;
        }
        if ctx.read >= ctx.queue.len() {
            ctx.queue.clear();
            ctx.read = 0;
        }
        count as i32
    })
    .unwrap_or(0)
}

#[no_mangle]
pub unsafe extern "system" fn WTPacketsPeek(
    handle: *mut c_void,
    maximum: i32,
    output: *mut c_void,
) -> i32 {
    trace(&format!(
        "WTPacketsPeek h=0x{:X} max={} out_null={}",
        handle as usize, maximum, output.is_null()
    ));
    if maximum <= 0 || output.is_null() {
        return 0;
    }
    with_context(handle, |ctx| {
        refresh(ctx, handle as usize);
        let count = ctx.queue.len().saturating_sub(ctx.read).min(maximum as usize);
        let width = packet_layout(ctx.packet_data).0;
        let mut write = output as *mut u8;
        for index in 0..count {
            let packet = &ctx.queue[ctx.read + index].bytes;
            unsafe {
                ptr::copy_nonoverlapping(packet.as_ptr(), write, packet.len().min(width));
                write = write.add(width);
            }
        }
        count as i32
    })
    .unwrap_or(0)
}

#[no_mangle]
pub unsafe extern "system" fn WTDataGet(
    handle: *mut c_void,
    _first: u32,
    _last: u32,
    maximum: i32,
    output: *mut c_void,
    count: *mut i32,
) -> i32 {
    let got = unsafe { WTPacketsGet(handle, maximum, output) };
    if !count.is_null() {
        unsafe { ptr::write_unaligned(count, got) };
    }
    got
}

#[no_mangle]
pub unsafe extern "system" fn WTDataPeek(
    handle: *mut c_void,
    _first: u32,
    _last: u32,
    maximum: i32,
    output: *mut c_void,
    count: *mut i32,
) -> i32 {
    let got = unsafe { WTPacketsPeek(handle, maximum, output) };
    if !count.is_null() {
        unsafe { ptr::write_unaligned(count, got) };
    }
    got
}

#[no_mangle]
pub unsafe extern "system" fn WTQueuePacketsEx(
    handle: *mut c_void,
    first: *mut u32,
    last: *mut u32,
) -> i32 {
    trace(&format!("WTQueuePacketsEx h=0x{:X}", handle as usize));
    if first.is_null() || last.is_null() {
        return 0;
    }
    with_context(handle, |ctx| {
        refresh(ctx, handle as usize);
        // Applications treat zero as "no packet" and throw, so report real
        // serials: the oldest pending packet and the newest queued one.
        let first_serial = ctx.queue.get(ctx.read).map(|p| p.serial).unwrap_or(0);
        let last_serial = ctx.queue.last().map(|p| p.serial).unwrap_or(first_serial);
        unsafe {
            ptr::write_unaligned(first, first_serial);
            ptr::write_unaligned(last, last_serial);
        }
    })
    .map_or(0, |_| 1)
}

#[no_mangle]
pub unsafe extern "system" fn WTMgrOpen(_hwnd: HWND, _base: u32) -> *mut c_void {
    open_context(
        default_mask(),
        TPS_NONE,
        true,
        0,
        0x7FF0,
        CXO_SYSTEM,
        screen_origin(),
        screen_extent(),
    )
}

#[no_mangle]
pub unsafe extern "system" fn WTMgrClose(handle: *mut c_void) -> i32 {
    unsafe { WTClose(handle) }
}

#[no_mangle]
pub unsafe extern "system" fn WTMgrDefContext(_manager: *mut c_void, _system: i32) -> *mut c_void {
    open_context(
        default_mask(),
        TPS_NONE,
        true,
        0,
        0x7FF0,
        CXO_SYSTEM,
        screen_origin(),
        screen_extent(),
    )
}

#[no_mangle]
pub unsafe extern "system" fn WTMgrDefContextEx(
    _manager: *mut c_void,
    _device: u32,
    _system: i32,
) -> *mut c_void {
    open_context(
        default_mask(),
        TPS_NONE,
        true,
        0,
        0x7FF0,
        CXO_SYSTEM,
        screen_origin(),
        screen_extent(),
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn layout_follows_the_context_mask_not_a_fixed_struct() {
        // Compact mask without PK_STATUS: the flexible PACKET starts at
        // pkCursor. Adding an unrequested pkStatus would shift every field.
        let mask = PK_CURSOR | PK_BUTTONS | PK_X | PK_Y | PK_NORMAL_PRESSURE | PK_ORIENTATION;
        let (width, slots) = packet_layout(mask);
        let at = |bit: u32| slots.iter().find(|(b, _)| *b == bit).unwrap().1;

        assert_eq!(width, 32);
        assert_eq!(at(PK_CURSOR), 0);
        assert_eq!(at(PK_BUTTONS), 4);
        assert_eq!(at(PK_X), 8);
        assert_eq!(at(PK_Y), 12);
        assert_eq!(at(PK_NORMAL_PRESSURE), 16);
        assert_eq!(at(PK_ORIENTATION), 20);
    }

    #[test]
    fn default_context_advertises_the_full_packet() {
        // Hardware drivers give WTI_DEFCONTEXT the complete mask. Applications
        // that copy it verbatim (PaintTool SAI) then read fields at the offsets
        // of the compile-time PACKET struct, so a trimmed default silently
        // shifts every field and pressure reads as zero.
        let mask = default_mask();
        let (width, slots) = packet_layout(mask);
        let at = |bit: u32| slots.iter().find(|(b, _)| *b == bit).unwrap().1;

        for bit in [
            PK_CONTEXT,
            PK_STATUS,
            PK_TIME,
            PK_CHANGED,
            PK_SERIAL_NUMBER,
            PK_CURSOR,
            PK_BUTTONS,
            PK_X,
            PK_Y,
            PK_Z,
            PK_NORMAL_PRESSURE,
            PK_TANGENT_PRESSURE,
            PK_ORIENTATION,
            PK_ROTATION,
        ] {
            assert_ne!(mask & bit, 0, "default mask is missing 0x{bit:X}");
        }

        if std::mem::size_of::<usize>() == 8 {
            assert_eq!(width, 80, "sizeof(PACKET) on x64, pointer-aligned");
            assert_eq!(at(PK_TIME), 12);
            assert_eq!(at(PK_NORMAL_PRESSURE), 44);
            assert_eq!(at(PK_ROTATION), 64);
        } else {
            assert_eq!(width, 72, "sizeof(PACKET) on x86");
            assert_eq!(at(PK_NORMAL_PRESSURE), 40);
            assert_eq!(at(PK_ROTATION), 60);
        }
    }

    #[test]
    fn transition_codes_latch_into_a_held_mask() {
        let mut pen = PenState::default();
        pen.latch_buttons(0x0002_0000, true); // TBN_DOWN, button 0 = tip
        assert_eq!(pen.latched_buttons, 1);
        pen.latch_buttons(0, true); // still held, no new transition
        assert_eq!(pen.latched_buttons, 1);
        pen.latch_buttons(0x0002_0001, true); // TBN_DOWN, button 1 = barrel
        assert_eq!(pen.latched_buttons, 3);
        pen.latch_buttons(0x0001_0000, true); // TBN_UP, tip
        assert_eq!(pen.latched_buttons, 2);
        pen.latch_buttons(0, false); // out of range clears everything
        assert_eq!(pen.latched_buttons, 0);

        // A plugin that publishes the mask directly is used as-is.
        let mut mask_pen = PenState::default();
        mask_pen.latch_buttons(5, true);
        assert_eq!(mask_pen.latched_buttons, 5);
        mask_pen.latch_buttons(0, true);
        assert_eq!(mask_pen.latched_buttons, 0);
    }

    #[test]
    fn pressure_lands_where_the_application_reads_it() {
        let mask = PK_CURSOR | PK_BUTTONS | PK_X | PK_Y | PK_NORMAL_PRESSURE | PK_ORIENTATION;
        let mut pen = PenState::default();
        pen.pressure = 20_686;
        pen.x = 1234;
        pen.y = 5678;
        pen.buttons = 2;
        pen.latched_buttons = 2;

        let packet = serialize(mask, &pen, 0x99, 0, PacketSpace { screen_px: false, sys_ext: [0, 0], sys_org: [0, 0] });
        let u = |at: usize| u32::from_le_bytes(packet[at..at + 4].try_into().unwrap());

        assert_eq!(packet.len(), 32);
        assert_eq!(u(16), scale_pressure(20_686), "pressure");
        assert_eq!(u(8) as i32, 1234, "x");
        assert_eq!(u(12) as i32, 5678, "y");
        assert_eq!(u(4), 2, "buttons");
    }

    #[test]
    fn photoshop_mask_matches_the_pktdef_field_order() {
        // Photoshop CC 2019 opens with 0x1FFC, which has no PK_STATUS but does
        // have PK_TIME, PK_TANGENT_PRESSURE and PK_ORIENTATION. Per Wintab's
        // flexible PACKET, the 52-byte struct is:
        // time(0), changed(4), serial(8), cursor(12), buttons(16), x(20),
        // y(24), z(28), normalPressure(32), tangent(36), orientation(40..52).
        let mask = 0x1FFC;
        let (width, slots) = packet_layout(mask);
        let at = |bit: u32| slots.iter().find(|(b, _)| *b == bit).unwrap().1;

        assert!(!slots.iter().any(|(b, _)| *b == PK_STATUS));
        assert_eq!(at(PK_TIME), 0);
        assert_eq!(at(PK_BUTTONS), 16);
        assert_eq!(at(PK_Z), 28);
        assert_eq!(at(PK_NORMAL_PRESSURE), 32);
        assert_eq!(at(PK_TANGENT_PRESSURE), 36);
        assert_eq!(at(PK_ORIENTATION), 40);
        assert_eq!(width, 52);
    }

    #[test]
    fn full_mask_still_produces_the_standard_struct() {
        let (width, slots) = packet_layout(default_mask_with_all());
        let at = |bit: u32| slots.iter().find(|(b, _)| *b == bit).unwrap().1;

        let hctx = std::mem::size_of::<usize>();
        assert_eq!(at(PK_STATUS), hctx);
        assert_eq!(at(PK_NORMAL_PRESSURE), hctx + 36);
        // Tangent pressure is a UINT in pktdef.h, not a 64-bit field: the old
        // 8-byte entry shifted orientation and rotation by four bytes.
        assert_eq!(at(PK_TANGENT_PRESSURE), hctx + 40);
        assert_eq!(at(PK_ORIENTATION), hctx + 44);
        assert_eq!(at(PK_ROTATION), hctx + 56);
        // sizeof(PACKET) rounds the 76-byte x64 struct up to pointer alignment.
        let size = hctx + 68;
        assert_eq!(width, (size + hctx - 1) & !(hctx - 1));
    }

    /// Every member, for the standard-layout test.
    fn default_mask_with_all() -> u32 {
        PK_CONTEXT
            | PK_STATUS
            | PK_TIME
            | PK_CHANGED
            | PK_SERIAL_NUMBER
            | PK_CURSOR
            | PK_BUTTONS
            | PK_X
            | PK_Y
            | PK_Z
            | PK_NORMAL_PRESSURE
            | PK_TANGENT_PRESSURE
            | PK_ORIENTATION
            | PK_ROTATION
    }

    #[test]
    fn wide_default_context_has_its_extents_at_the_w_layout() {
        // WTInfoW must fill LOGCONTEXTW (80-byte name). Returning LOGCONTEXTA instead
        // shifted every field by 40 bytes: an app then read in-ext as 1x1 and could
        // not map tablet units onto its canvas at all.
        let mut buf = vec![0u8; 512];
        let written = unsafe { WTInfoW(WTI_DEFCONTEXT, 0, buf.as_mut_ptr().cast()) };
        assert_eq!(written as usize, std::mem::size_of::<LogContextW>());
        assert!(std::mem::size_of::<LogContextW>() > std::mem::size_of::<LogContextA>());

        // name (80) + 11 x u32 (44) = 124; then in_org, in_ext, out_org, out_ext,
        // each [i32; 3].
        let i32_at = |offset: usize| -> i32 {
            i32::from_le_bytes(buf[offset..offset + 4].try_into().unwrap())
        };
        let (ext_x, ext_y) = device_extent();
        assert_eq!(i32_at(124), 0, "in_org x");
        assert_eq!(i32_at(136), ext_x, "in_ext x");
        assert_eq!(i32_at(140), ext_y, "in_ext y");
        assert_eq!(i32_at(160), ext_x, "out_ext x");
        assert_eq!(i32_at(164), ext_y, "out_ext y");
    }

    #[test]
    fn wide_and_narrow_contexts_differ_in_name_width() {
        assert_eq!(
            std::mem::size_of::<LogContextW>() - std::mem::size_of::<LogContextA>(),
            40,
            "LOGCONTEXTW reserves 40 UTF-16 chars, LOGCONTEXTA 40 bytes"
        );
    }

    #[test]
    fn the_device_and_cursor_declaration_lists_every_standard_item() {
        // An application asks "what is this device" through these items. A zero
        // answer to any standard one is a hole in the declaration, and a hole is
        // what makes a painting program conclude the tablet has no pressure even
        // though packets carry it.
        for item in 1..=DVC_MAX {
            // Wacom leaves DVC_ROTATION unimplemented on tablets without a
            // twist sensor (the Intuos Pro M reports size 0 here), so a zero
            // answer is correct for that item only.
            if item == DVC_ROTATION {
                continue;
            }
            let mut buf = vec![0u8; 256];
            let size = unsafe { WTInfoW(WTI_DEVICES, item, buf.as_mut_ptr().cast()) };
            assert!(size > 0, "device item {item} returned nothing");
        }
        // The pressure declaration has to be read off the cursor the pen lives
        // in. Wacom puts the "Pressure Stylus" at index 1 and leaves cursor 0
        // as a button-only puck, so probing category 200 for pressure items is
        // exactly the mistake that made Photoshop classify the pen as
        // non-pressure-capable.
        let pen_cursor = WTI_CURSORS + PEN_CURSOR_INDEX;
        for item in 1..=CSR_MAX {
            // The official Wacom driver leaves the tangential-pressure items
            // and CSR_MINPKTDATA/CSR_MINBUTTONS unimplemented; a zero size is
            // the correct answer there and tells applications to use raw
            // pressure.
            if item == CSR_TPRESPONSE
                || item == CSR_TPBUTTON
                || item == CSR_TPBTNMARKS
                || item == CSR_MINPKTDATA
                || item == CSR_MINBUTTONS
            {
                continue;
            }
            let mut buf = vec![0u8; 2048];
            let size = unsafe { WTInfoW(pen_cursor, item, buf.as_mut_ptr().cast()) };
            assert!(size > 0, "pen cursor item {item} returned nothing");
            assert!(size as usize <= buf.len(), "cursor item {item} overflowed the probe buffer");
        }
        // Puck cursor still answers its own identity, it just has no pressure.
        let mut buf = vec![0u8; 2048];
        let size = unsafe { WTInfoW(WTI_CURSORS, CSR_TYPE, buf.as_mut_ptr().cast()) };
        assert!(size > 0, "puck cursor type returned nothing");
        let ptype = u32::from_le_bytes(buf[..4].try_into().unwrap());
        assert_eq!(ptype, 0x8000, "cursor 0 is the puck");
        let mut buf = vec![0u8; 2048];
        let size = unsafe { WTInfoW(pen_cursor, CSR_TYPE, buf.as_mut_ptr().cast()) };
        assert!(size > 0, "pen cursor type returned nothing");
        let pen_type = u32::from_le_bytes(buf[..4].try_into().unwrap());
        assert_eq!(pen_type, 0x4842, "cursor 1 is the pressure stylus");
        for item in 1..=STA_MAX {
            let mut buf = vec![0u8; 256];
            let size = unsafe { WTInfoW(WTI_STATUS, item, buf.as_mut_ptr().cast()) };
            assert!(size > 0, "status item {item} returned nothing");
        }
        for item in 1..=10 {
            let mut buf = vec![0u8; 256];
            let size = unsafe { WTInfoW(WTI_INTERFACE, item, buf.as_mut_ptr().cast()) };
            assert!(size > 0, "interface item {item} returned nothing");
        }
    }

    #[test]
    fn spec_version_is_a_single_word_of_one_point_four() {
        let mut buf = vec![0u8; 16];
        let size = unsafe { WTInfoW(WTI_INTERFACE, IFC_SPECVERSION, buf.as_mut_ptr().cast()) };
        assert_eq!(size, 2, "IFC_SPECVERSION is a WORD");
        let value = u16::from_le_bytes(buf[..2].try_into().unwrap());
        assert_eq!(value, 0x0104, "high byte major, low byte minor");
    }

    #[test]
    fn category_zero_probe_reports_the_largest_category_size() {
        // Photoshop's first and only Wintab call is WTInfo(0, 0, NULL). Zero
        // here makes it decide there is no tablet and fall back to Windows Ink.
        let size_w = unsafe { WTInfoW(0, 0, std::ptr::null_mut()) };
        let size_a = unsafe { WTInfoA(0, 0, std::ptr::null_mut()) };
        assert_eq!(size_w, std::mem::size_of::<LogContextW>() as u32);
        assert_eq!(size_a, size_w);
        assert!(size_w > 0);
    }
}

#[cfg(test)]
mod conformance {
    //! End-to-end checks against a synthetic sample source. They exercise the
    //! exported functions exactly as an application would, with no tablet and
    //! no C# tooling involved.

    use super::*;
    use std::ptr;
    use std::sync::atomic::Ordering;
    use windows_sys::Win32::{
        Foundation::{HWND, INVALID_HANDLE_VALUE},
        System::{
            Memory::{
                CreateFileMappingW, MapViewOfFile, OpenFileMappingW, FILE_MAP_ALL_ACCESS,
                PAGE_READWRITE,
            },
            SystemInformation::GetTickCount,
        },
    };

    fn raw_pressure(n: u32) -> u32 {
        if in_range(n) {
            // 1000..~31000 in the driver's raw 0..32767 range.
            1000 + (n * 97) % 30_000
        } else {
            0
        }
    }

    fn expected_pressure(n: u32) -> u32 {
        scale_pressure(raw_pressure(n))
    }

    fn in_range(n: u32) -> bool {
        n % 100 < 60
    }

    struct Feeder {
        state: *mut SharedState,
    }

    impl Feeder {
        /// Creates the mapping the shim reads. Returns None when another process
        /// (a running OpenTabletDriver daemon) already owns it: writing synthetic
        /// samples into a live session would move the user's cursor.
        fn new() -> Option<Feeder> {
            let mut name: Vec<u16> = SHARED_NAME.encode_utf16().collect();
            name.push(0);

            let existing = unsafe { OpenFileMappingW(FILE_MAP_ALL_ACCESS, 0, name.as_ptr()) };
            if !existing.is_null() {
                return None;
            }

            let size = std::mem::size_of::<SharedState>() as u32;
            let handle = unsafe {
                CreateFileMappingW(
                    INVALID_HANDLE_VALUE,
                    ptr::null(),
                    PAGE_READWRITE,
                    0,
                    size,
                    name.as_ptr(),
                )
            };
            assert!(!handle.is_null(), "CreateFileMappingW failed");

            let view = unsafe { MapViewOfFile(handle, FILE_MAP_ALL_ACCESS, 0, 0, size as usize) };
            let state = view.Value as *mut SharedState;
            assert!(!state.is_null(), "MapViewOfFile failed");

            unsafe {
                (*state).magic = SHARED_MAGIC;
                (*state).version = 1;
                (*state).enabled = 1;
                (*state).width = MAX_X;
                (*state).height = MAX_Y;
                (*state).max_pressure = MAX_PRESSURE;
                (*state).heartbeat_ms = GetTickCount();
                (*state).write_index.store(0, Ordering::Release);
            }

            Some(Feeder { state })
        }

        fn publish(&self, n: u32) {
            let range = in_range(n);
            unsafe {
                let slot = n as usize % RING_CAPACITY;
                let packet = &mut (*self.state).packets[slot];
                packet.sequence = n;
                packet.flags = 0;
                packet.x = 1000 + (n % 40_000) as i32;
                packet.y = 900 + ((n * 23) % 25_000) as i32;
                packet.pressure = raw_pressure(n);
                packet.tilt_x = 20;
                packet.tilt_y = 10;
                packet.rotation = 300;
                packet.buttons = 1;
                packet.cursor = 0;
                packet.time_ms = GetTickCount();
                packet.in_range = if range { 1 } else { 0 };

                (*self.state).heartbeat_ms = GetTickCount();
                (*self.state).write_index.store(n, Ordering::Release);
            }
        }
    }

    fn u32_at(bytes: &[u8], at: usize) -> u32 {
        u32::from_le_bytes(bytes[at..at + 4].try_into().unwrap())
    }

    fn i32_at(bytes: &[u8], at: usize) -> i32 {
        i32::from_le_bytes(bytes[at..at + 4].try_into().unwrap())
    }

    #[test]
    fn conformance_end_to_end() {
        let Some(feeder) = Feeder::new() else {
            eprintln!("skipping conformance: the shared mapping is already in use");
            return;
        };
        let hwnd: HWND = unsafe { std::mem::zeroed() };

        // ------------------------------------------------------------ WTInfo
        let mut options = 0u32;
        unsafe { WTInfoW(WTI_INTERFACE, IFC_CTXOPTIONS, (&mut options as *mut u32).cast()) };
        assert_ne!(
            options & CXO_MESSAGES,
            0,
            "IFC_CTXOPTIONS must advertise CXO_MESSAGES"
        );

        let mut pkt_data = 0u32;
        unsafe { WTInfoW(WTI_DEVICES, DVC_PKTDATA, (&mut pkt_data as *mut u32).cast()) };
        assert_eq!(pkt_data, 0x1FF, "DVC_PKTDATA must match Wacom's always-available mask");
        let mut csr_data = 0u32;
        unsafe { WTInfoW(WTI_DEVICES, DVC_CSRDATA, (&mut csr_data as *mut u32).cast()) };
        assert_eq!(
            csr_data,
            PK_NORMAL_PRESSURE | PK_TANGENT_PRESSURE | PK_ORIENTATION,
            "DVC_CSRDATA must advertise the per-cursor pressure/tilt data"
        );

        let mut axes = [0u8; 48];
        let written = unsafe { WTInfoW(WTI_DEVICES, DVC_ORIENTATION, axes.as_mut_ptr().cast()) };
        assert_eq!(written, 48, "DVC_ORIENTATION must return three AXIS entries");
        assert_eq!(i32_at(&axes, 4), 3600, "azimuth max");
        assert_eq!(i32_at(&axes, 16), -900, "altitude min");
        assert_eq!(i32_at(&axes, 20), 900, "altitude max");
        assert_eq!(i32_at(&axes, 36), 3600, "twist max");

        let mut axis = [0u8; 16];
        unsafe { WTInfoW(WTI_DEVICES, DVC_X, axis.as_mut_ptr().cast()) };
        assert_eq!(i32_at(&axis, 4), MAX_X - 1, "DVC_X follows shared state");
        assert_eq!(i32_at(&axis, 8), TU_CM as i32, "DVC_X units");

        let mut default_ctx = base_context_w();
        let written =
            unsafe { WTInfoW(WTI_DEFCONTEXT, 0, (&mut default_ctx as *mut LogContextW).cast()) };
        assert_eq!(written as usize, std::mem::size_of::<LogContextW>());
        assert_eq!(default_ctx.in_ext[0], MAX_X, "default in-extent");
        assert_eq!(default_ctx.out_ext[1], MAX_Y, "default out-extent");

        // ------------------------------------------- PhotoCraft-style compact mask
        let photo_mask =
            PK_CURSOR | PK_BUTTONS | PK_X | PK_Y | PK_NORMAL_PRESSURE | PK_ORIENTATION;
        assert_eq!(packet_layout(photo_mask).0, 32);

        let mut ctx = base_context_w();
        ctx.pkt_data = photo_mask;
        let handle = unsafe { WTOpenW(hwnd, &ctx, 1) };
        assert!(!handle.is_null(), "WTOpenW failed for the compact mask");

        for n in 1..=300 {
            feeder.publish(n);
        }

        let mut buffer = vec![0u8; 32 * 64];
        let mut pressures = Vec::new();
        loop {
            let count = unsafe { WTPacketsGet(handle, 64, buffer.as_mut_ptr().cast()) };
            if count == 0 {
                break;
            }
            for i in 0..count as usize {
                pressures.push(u32_at(&buffer, i * 32 + 16));
            }
        }
        unsafe { WTClose(handle) };

        assert!(pressures.len() >= 64, "compact mask delivered {} packets", pressures.len());
        // The compact mask carries the same scaled 0..MAX_PRESSURE value as
        // every other layout. The shared ring is intentionally bounded, so the
        // last packet is not guaranteed to survive into the queue; assert that
        // the queue contains a high, varying pressure rather than one exact
        // endpoint sample.
        let max_pressure = pressures.iter().copied().max().unwrap_or(0);
        let min_pressure = pressures.iter().copied().min().unwrap_or(0);
        assert!(
            max_pressure >= 20_000,
            "compact mask pressure never rose: max {max_pressure}"
        );
        assert!(
            max_pressure > min_pressure,
            "compact mask pressure was flat: min {min_pressure}, max {max_pressure}"
        );

        // ---------------------------------------------- Photoshop-style full mask
        let photo_shop_mask = PK_STATUS
            | PK_TIME
            | PK_CHANGED
            | PK_SERIAL_NUMBER
            | PK_CURSOR
            | PK_BUTTONS
            | PK_X
            | PK_Y
            | PK_Z
            | PK_NORMAL_PRESSURE
            | PK_TANGENT_PRESSURE
            | PK_ORIENTATION
            | PK_ROTATION;
        assert_eq!(packet_layout(photo_shop_mask).0, 68);

        let mut ctx = base_context_w();
        ctx.pkt_data = photo_shop_mask;
        let handle = unsafe { WTOpenW(hwnd, &ctx, 1) };
        assert!(!handle.is_null(), "WTOpenW failed for the Photoshop mask");

        for n in 301..=600 {
            feeder.publish(n);
        }

        let mut buffer = vec![0u8; 68 * 64];
        let mut seen = 0usize;
        let mut in_range_packets = 0usize;
        let mut out_of_range_packets = 0usize;
        loop {
            let count = unsafe { WTPacketsGet(handle, 64, buffer.as_mut_ptr().cast()) };
            if count == 0 {
                break;
            }
            for i in 0..count as usize {
                let at = i * 68;
                let status = u32_at(&buffer, at);
                let serial = u32_at(&buffer, at + 12);
                let sample = u32_at(&buffer, at + 24).saturating_sub(1000);
                let pressure = u32_at(&buffer, at + 36);
                let azimuth = i32_at(&buffer, at + 44);
                let altitude = i32_at(&buffer, at + 48);

                // The serial is the driver's own counter and must advance by one
                // per packet; the feeder's sample index travels in pkX, so
                // expectations are keyed on that instead.
                assert_eq!(
                    pressure,
                    expected_pressure(sample),
                    "pressure for serial {serial} (sample {sample}) landed at the wrong offset"
                );
                if in_range(sample) {
                    in_range_packets += 1;
                    assert_eq!(status, 0, "in-range packet reported status 0x{status:X}");
                } else {
                    out_of_range_packets += 1;
                    assert_eq!(status, TPS_PROXIMITY, "out-of-range packet must set TPS_PROXIMITY");
                }
                assert!((0..=3600).contains(&azimuth), "azimuth {azimuth} out of range");
                assert!((0..=900).contains(&altitude), "altitude {altitude} out of range");
                seen += 1;
            }
        }
        unsafe { WTClose(handle) };

        // 300 samples, 60% of them in range: out-of-range runs contribute only
        // their exit transition, so 183 packets is the expected order of
        // magnitude, not 300.
        assert!(seen >= 120, "Photoshop mask delivered only {seen} packets");
        assert!(in_range_packets > 0 && out_of_range_packets > 0, "status was never exercised");

        // -------------------------------------------------- WTPacket by serial
        let mut ctx = base_context_w();
        ctx.pkt_data = photo_shop_mask;
        let handle = unsafe { WTOpenW(hwnd, &ctx, 1) };
        assert!(!handle.is_null(), "WTOpenW failed for the WTPacket test");

        for n in 601..=700 {
            feeder.publish(n);
        }

        let mut first = 0u32;
        let mut last = 0u32;
        let queued = unsafe { WTQueuePacketsEx(handle, &mut first, &mut last) };
        assert_ne!(queued, 0, "WTQueuePacketsEx failed");
        assert!(first != 0 && last >= first, "queue head serial {first}, tail {last}");

        let mut packet = vec![0u8; 68];
        assert_eq!(
            unsafe { WTPacket(handle, first - 1, packet.as_mut_ptr().cast()) },
            0,
            "an already-consumed serial must not produce a packet"
        );
        assert_eq!(
            unsafe { WTPacket(handle, last + 500, packet.as_mut_ptr().cast()) },
            0,
            "a future serial must not produce a packet"
        );
        assert_eq!(
            unsafe { WTPacket(handle, first, packet.as_mut_ptr().cast()) },
            1,
            "WTPacket must return the requested serial"
        );
        assert_eq!(u32_at(&packet, 12), first, "returned packet carries another serial");
        let returned_sample = u32_at(&packet, 24).saturating_sub(1000);
        assert_eq!(
            u32_at(&packet, 36),
            expected_pressure(returned_sample),
            "returned packet carries another sample's pressure"
        );
        // Applications ask for the same serial again while painting; the second
        // lookup must still resolve, otherwise the caller keeps a stale
        // pressure value.
        assert_eq!(
            unsafe { WTPacket(handle, first, packet.as_mut_ptr().cast()) },
            1,
            "a serial already handed over must stay resolvable"
        );

        unsafe { WTClose(handle) };
    }
}

#[no_mangle]
pub extern "system" fn Sync() {}

#[no_mangle]
unsafe extern "system" fn DllMain(_instance: HINSTANCE, reason: u32, _reserved: *mut c_void) -> i32 {
    if reason == DLL_PROCESS_ATTACH {
        VIEW.store(ptr::null_mut(), Ordering::Release);
    }
    1
}

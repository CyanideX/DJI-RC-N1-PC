"""
DJI RC-N1 Virtual Gamepad Bridge - Tkinter UI

Visual interface showing connection status, serial port,
and real-time stick position visualization with numeric values.

Requirements:
    pip install pyserial vgamepad
    ViGEmBus driver installed
"""

import ctypes
import struct
import sys
import time
import tkinter as tk
from threading import Thread, Event

# Enable per-monitor DPI awareness on Windows (fixes blurry text on 4K displays)
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except (AttributeError, OSError):
    pass

import serial
import serial.tools.list_ports
import vgamepad as vg


# CRC-16 lookup table (CCITT variant)
CRC16_TABLE = (
    0x0000, 0x1189, 0x2312, 0x329B, 0x4624, 0x57AD, 0x6536, 0x74BF,
    0x8C48, 0x9DC1, 0xAF5A, 0xBED3, 0xCA6C, 0xDBE5, 0xE97E, 0xF8F7,
    0x1081, 0x0108, 0x3393, 0x221A, 0x56A5, 0x472C, 0x75B7, 0x643E,
    0x9CC9, 0x8D40, 0xBFDB, 0xAE52, 0xDAED, 0xCB64, 0xF9FF, 0xE876,
    0x2102, 0x308B, 0x0210, 0x1399, 0x6726, 0x76AF, 0x4434, 0x55BD,
    0xAD4A, 0xBCC3, 0x8E58, 0x9FD1, 0xEB6E, 0xFAE7, 0xC87C, 0xD9F5,
    0x3183, 0x200A, 0x1291, 0x0318, 0x77A7, 0x662E, 0x54B5, 0x453C,
    0xBDCB, 0xAC42, 0x9ED9, 0x8F50, 0xFBEF, 0xEA66, 0xD8FD, 0xC974,
    0x4204, 0x538D, 0x6116, 0x709F, 0x0420, 0x15A9, 0x2732, 0x36BB,
    0xCE4C, 0xDFC5, 0xED5E, 0xFCD7, 0x8868, 0x99E1, 0xAB7A, 0xBAF3,
    0x5285, 0x430C, 0x7197, 0x601E, 0x14A1, 0x0528, 0x37B3, 0x263A,
    0xDECD, 0xCF44, 0xFDDF, 0xEC56, 0x98E9, 0x8960, 0xBBFB, 0xAA72,
    0x6306, 0x728F, 0x4014, 0x519D, 0x2522, 0x34AB, 0x0630, 0x17B9,
    0xEF4E, 0xFEC7, 0xCC5C, 0xDDD5, 0xA96A, 0xB8E3, 0x8A78, 0x9BF1,
    0x7387, 0x620E, 0x5095, 0x411C, 0x35A3, 0x242A, 0x16B1, 0x0738,
    0xFFCF, 0xEE46, 0xDCDD, 0xCD54, 0xB9EB, 0xA862, 0x9AF9, 0x8B70,
    0x8408, 0x9581, 0xA71A, 0xB693, 0xC22C, 0xD3A5, 0xE13E, 0xF0B7,
    0x0840, 0x19C9, 0x2B52, 0x3ADB, 0x4E64, 0x5FED, 0x6D76, 0x7CFF,
    0x9489, 0x8500, 0xB79B, 0xA612, 0xD2AD, 0xC324, 0xF1BF, 0xE036,
    0x18C1, 0x0948, 0x3BD3, 0x2A5A, 0x5EE5, 0x4F6C, 0x7DF7, 0x6C7E,
    0xA50A, 0xB483, 0x8618, 0x9791, 0xE32E, 0xF2A7, 0xC03C, 0xD1B5,
    0x2942, 0x38CB, 0x0A50, 0x1BD9, 0x6F66, 0x7EEF, 0x4C74, 0x5DFD,
    0xB58B, 0xA402, 0x9699, 0x8710, 0xF3AF, 0xE226, 0xD0BD, 0xC134,
    0x39C3, 0x284A, 0x1AD1, 0x0B58, 0x7FE7, 0x6E6E, 0x5CF5, 0x4D7C,
    0xC60C, 0xD785, 0xE51E, 0xF497, 0x8028, 0x91A1, 0xA33A, 0xB2B3,
    0x4A44, 0x5BCD, 0x6956, 0x78DF, 0x0C60, 0x1DE9, 0x2F72, 0x3EFB,
    0xD68D, 0xC704, 0xF59F, 0xE416, 0x90A9, 0x8120, 0xB3BB, 0xA232,
    0x5AC5, 0x4B4C, 0x79D7, 0x685E, 0x1CE1, 0x0D68, 0x3FF3, 0x2E7A,
    0xE70E, 0xF687, 0xC41C, 0xD595, 0xA12A, 0xB0A3, 0x8238, 0x93B1,
    0x6B46, 0x7ACF, 0x4854, 0x59DD, 0x2D62, 0x3CEB, 0x0E70, 0x1FF9,
    0xF78F, 0xE606, 0xD49D, 0xC514, 0xB1AB, 0xA022, 0x92B9, 0x8330,
    0x7BC7, 0x6A4E, 0x58D5, 0x495C, 0x3DE3, 0x2C6A, 0x1EF1, 0x0F78,
)

CRC8_TABLE = bytes([
    0x00, 0x5E, 0xBC, 0xE2, 0x61, 0x3F, 0xDD, 0x83,
    0xC2, 0x9C, 0x7E, 0x20, 0xA3, 0xFD, 0x1F, 0x41,
    0x9D, 0xC3, 0x21, 0x7F, 0xFC, 0xA2, 0x40, 0x1E,
    0x5F, 0x01, 0xE3, 0xBD, 0x3E, 0x60, 0x82, 0xDC,
    0x23, 0x7D, 0x9F, 0xC1, 0x42, 0x1C, 0xFE, 0xA0,
    0xE1, 0xBF, 0x5D, 0x03, 0x80, 0xDE, 0x3C, 0x62,
    0xBE, 0xE0, 0x02, 0x5C, 0xDF, 0x81, 0x63, 0x3D,
    0x7C, 0x22, 0xC0, 0x9E, 0x1D, 0x43, 0xA1, 0xFF,
    0x46, 0x18, 0xFA, 0xA4, 0x27, 0x79, 0x9B, 0xC5,
    0x84, 0xDA, 0x38, 0x66, 0xE5, 0xBB, 0x59, 0x07,
    0xDB, 0x85, 0x67, 0x39, 0xBA, 0xE4, 0x06, 0x58,
    0x19, 0x47, 0xA5, 0xFB, 0x78, 0x26, 0xC4, 0x9A,
    0x65, 0x3B, 0xD9, 0x87, 0x04, 0x5A, 0xB8, 0xE6,
    0xA7, 0xF9, 0x1B, 0x45, 0xC6, 0x98, 0x7A, 0x24,
    0xF8, 0xA6, 0x44, 0x1A, 0x99, 0xC7, 0x25, 0x7B,
    0x3A, 0x64, 0x86, 0xD8, 0x5B, 0x05, 0xE7, 0xB9,
    0x8C, 0xD2, 0x30, 0x6E, 0xED, 0xB3, 0x51, 0x0F,
    0x4E, 0x10, 0xF2, 0xAC, 0x2F, 0x71, 0x93, 0xCD,
    0x11, 0x4F, 0xAD, 0xF3, 0x70, 0x2E, 0xCC, 0x92,
    0xD3, 0x8D, 0x6F, 0x31, 0xB2, 0xEC, 0x0E, 0x50,
    0xAF, 0xF1, 0x13, 0x4D, 0xCE, 0x90, 0x72, 0x2C,
    0x6D, 0x33, 0xD1, 0x8F, 0x0C, 0x52, 0xB0, 0xEE,
    0x32, 0x6C, 0x8E, 0xD0, 0x53, 0x0D, 0xEF, 0xB1,
    0xF0, 0xAE, 0x4C, 0x12, 0x91, 0xCF, 0x2D, 0x73,
    0xCA, 0x94, 0x76, 0x28, 0xAB, 0xF5, 0x17, 0x49,
    0x08, 0x56, 0xB4, 0xEA, 0x69, 0x37, 0xD5, 0x8B,
    0x57, 0x09, 0xEB, 0xB5, 0x36, 0x68, 0x8A, 0xD4,
    0x95, 0xCB, 0x29, 0x77, 0xF4, 0xAA, 0x48, 0x16,
    0xE9, 0xB7, 0x55, 0x0B, 0x88, 0xD6, 0x34, 0x6A,
    0x2B, 0x75, 0x97, 0xC9, 0x4A, 0x14, 0xF6, 0xA8,
    0x74, 0x2A, 0xC8, 0x96, 0x15, 0x4B, 0xA9, 0xF7,
    0xB6, 0xE8, 0x0A, 0x54, 0xD7, 0x89, 0x6B, 0x35,
])

# DUML protocol constants
CRC16_SEED = 0x3692
CRC8_SEED = 0x77
DUML_HEADER = 0x55
DUML_MIN_PACKET_LEN = 13
EXPECTED_STICK_PACKET_LEN = 38

# Controller input range
INPUT_CENTER = 1024
INPUT_RANGE = 660
GAMEPAD_MAX = 32767

# Camera wheel thresholds
CAMERA_BUTTON_THRESHOLD = 32000

# Stick state indices
RH = 0
RV = 1
LH = 2
LV = 3
CAM = 4

UINT16 = struct.Struct('<H')


def calc_crc16(packet, length):
    crc = CRC16_SEED
    table = CRC16_TABLE
    for i in range(length):
        crc = (crc >> 8) ^ table[(packet[i] ^ crc) & 0xFF]
    return crc


def calc_header_crc8(packet, length):
    crc = CRC8_SEED
    table = CRC8_TABLE
    for i in range(length):
        crc = table[(packet[i] ^ crc) & 0xFF]
    return crc


def build_duml_packet(source, target, cmd_type, cmd_set, cmd_id, seq, payload=None):
    length = DUML_MIN_PACKET_LEN
    if payload is not None:
        length += len(payload)

    packet = bytearray(length + 2)
    packet[0] = DUML_HEADER
    packet[1] = length & 0xFF
    packet[2] = (length >> 8) | 0x04
    packet[3] = calc_header_crc8(packet, 3)
    packet[4] = source
    packet[5] = target
    struct.pack_into('<H', packet, 6, seq)
    packet[8] = cmd_type
    packet[9] = cmd_set
    packet[10] = cmd_id

    if payload is not None:
        packet[11:11 + len(payload)] = payload

    crc = calc_crc16(packet, length)
    struct.pack_into('<H', packet, length, crc)
    return packet


def clamp_stick(raw):
    output = (raw - INPUT_CENTER) * GAMEPAD_MAX // INPUT_RANGE
    if output > 32767:
        return 32767
    if output < -32768:
        return -32768
    return output


def find_dji_port():
    ports = serial.tools.list_ports.comports(include_links=True)
    for port in ports:
        if "For Protocol" in (port.description or ""):
            try:
                ser = serial.Serial(port=port.name, baudrate=115200, timeout=1)
                return ser, port.name, port.description
            except (OSError, serial.SerialException):
                return None, port.name, port.description
    return None, None, None


def read_duml_packet(port):
    b = port.read(1)
    if not b or b[0] != DUML_HEADER:
        return None

    length_bytes = port.read(2)
    if len(length_bytes) < 2:
        return None

    packet_length = (length_bytes[0] | (length_bytes[1] << 8)) & 0x03FF
    remaining = port.read(packet_length - 3)
    if len(remaining) < packet_length - 3:
        return None

    buffer = bytearray(packet_length)
    buffer[0] = DUML_HEADER
    buffer[1] = length_bytes[0]
    buffer[2] = length_bytes[1]
    buffer[3:] = remaining
    return buffer


def gamepad_update_loop(gamepad, stick_state, stop_event):
    prev_cam_state = 0
    btn_y = vg.XUSB_BUTTON.XUSB_GAMEPAD_Y
    btn_b = vg.XUSB_BUTTON.XUSB_GAMEPAD_B

    while not stop_event.is_set():
        time.sleep(0.01)

        gamepad.left_joystick(stick_state[LH], stick_state[LV])
        gamepad.right_joystick(stick_state[RH], stick_state[RV])

        cam = stick_state[CAM]
        if cam > CAMERA_BUTTON_THRESHOLD:
            cam_state = 1
        elif cam < -CAMERA_BUTTON_THRESHOLD:
            cam_state = -1
        else:
            cam_state = 0

        if cam_state != prev_cam_state:
            if cam_state == 1:
                gamepad.press_button(btn_y)
                gamepad.release_button(btn_b)
            elif cam_state == -1:
                gamepad.press_button(btn_b)
                gamepad.release_button(btn_y)
            else:
                gamepad.release_button(btn_y)
                gamepad.release_button(btn_b)
            prev_cam_state = cam_state

        gamepad.update()


def serial_read_loop(port, stick_state, stop_event):
    """Background thread that polls the RC and updates stick_state."""
    poll_packet = build_duml_packet(0x0A, 0x06, 0x40, 0x06, 0x01, 0x34EB, bytearray())
    sim_enable_packet = build_duml_packet(0x0A, 0x06, 0x40, 0x06, 0x24, 0x34EB, bytearray([0x01]))

    try:
        port.write(sim_enable_packet)

        while not stop_event.is_set():
            port.write(poll_packet)
            data = read_duml_packet(port)
            if data is None:
                continue

            if len(data) == 21:
                stick_state[RH] = clamp_stick(UINT16.unpack_from(data, 11)[0])
                stick_state[RV] = clamp_stick(UINT16.unpack_from(data, 13)[0])
                stick_state[LV] = clamp_stick(UINT16.unpack_from(data, 15)[0])
                stick_state[LH] = clamp_stick(UINT16.unpack_from(data, 17)[0])

            elif len(data) == EXPECTED_STICK_PACKET_LEN:
                stick_state[RH] = clamp_stick(UINT16.unpack_from(data, 13)[0])
                stick_state[RV] = clamp_stick(UINT16.unpack_from(data, 16)[0])
                stick_state[LV] = clamp_stick(UINT16.unpack_from(data, 19)[0])
                stick_state[LH] = clamp_stick(UINT16.unpack_from(data, 22)[0])
                stick_state[CAM] = clamp_stick(UINT16.unpack_from(data, 25)[0])

    except serial.SerialException:
        pass


# --- UI ---

VERSION = "3.2.0"

CANVAS_SIZE = 280
STICK_RADIUS = 12
BG_COLOR = "#1e1e2e"
PANEL_COLOR = "#2a2a3c"
ACCENT_COLOR = "#89b4fa"
TEXT_COLOR = "#cdd6f4"
DIM_COLOR = "#6c7086"
CONNECTED_COLOR = "#a6e3a1"
DISCONNECTED_COLOR = "#f38ba8"


class StickCanvas:
    """A canvas widget that draws a stick position as a circle on a crosshair."""

    def __init__(self, parent, label):
        self.frame = tk.Frame(parent, bg=PANEL_COLOR, padx=20, pady=16)
        self.frame.pack(side=tk.LEFT, padx=20, pady=12)

        tk.Label(
            self.frame, text=label, font=("Segoe UI", 13, "bold"),
            fg=TEXT_COLOR, bg=PANEL_COLOR
        ).pack(pady=(12, 8))

        self.canvas = tk.Canvas(
            self.frame, width=CANVAS_SIZE, height=CANVAS_SIZE,
            bg=PANEL_COLOR, highlightthickness=0
        )
        self.canvas.pack()

        self.value_label = tk.Label(
            self.frame, text="X: 0  Y: 0", font=("Consolas", 12),
            fg=DIM_COLOR, bg=PANEL_COLOR
        )
        self.value_label.pack(pady=(8, 12))

        self._draw_background()
        self.dot = self.canvas.create_oval(0, 0, 0, 0, fill=ACCENT_COLOR, outline="")

    def _draw_background(self):
        c = CANVAS_SIZE // 2
        # Outer boundary circle
        self.canvas.create_oval(
            10, 10, CANVAS_SIZE - 10, CANVAS_SIZE - 10,
            outline=DIM_COLOR, width=1
        )
        # Crosshair
        self.canvas.create_line(c, 10, c, CANVAS_SIZE - 10, fill=DIM_COLOR, dash=(2, 4))
        self.canvas.create_line(10, c, CANVAS_SIZE - 10, c, fill=DIM_COLOR, dash=(2, 4))

    def update(self, x, y):
        """Update dot position. x/y in range [-32768, 32767]."""
        center = CANVAS_SIZE // 2
        radius = (CANVAS_SIZE // 2) - 10
        # Normalize to [-1, 1]
        nx = x / 32767.0
        ny = -y / 32767.0  # Invert Y so up is up on canvas
        # Clamp to unit circle
        mag = (nx * nx + ny * ny) ** 0.5
        if mag > 1.0:
            nx /= mag
            ny /= mag

        px = center + nx * radius
        py = center + ny * radius

        self.canvas.coords(
            self.dot,
            px - STICK_RADIUS, py - STICK_RADIUS,
            px + STICK_RADIUS, py + STICK_RADIUS,
        )
        self.value_label.config(text=f"X:{x:+6d}  Y:{y:+6d}")


class App:
    def __init__(self):
        self.root = tk.Tk()
        self.root.title("DJI RC-N1 Gamepad Bridge")
        self.root.configure(bg=BG_COLOR)
        self.root.resizable(False, False)

        self.stop_event = Event()
        self.stick_state = [0, 0, 0, 0, 0]
        self.port = None
        self.port_name = None
        self.port_description = None
        self.connected = False

        self._build_ui()
        self._try_connect()
        self._poll_ui()
        self.root.protocol("WM_DELETE_WINDOW", self._on_close)

    def _build_ui(self):
        # Status bar
        status_frame = tk.Frame(self.root, bg=BG_COLOR)
        status_frame.pack(fill=tk.X, padx=24, pady=(16, 0))

        self.status_dot = tk.Label(
            status_frame, text="\u2B24", font=("Segoe UI", 12),
            fg=DISCONNECTED_COLOR, bg=BG_COLOR
        )
        self.status_dot.pack(side=tk.LEFT)

        self.status_label = tk.Label(
            status_frame, text="Disconnected", font=("Segoe UI", 13),
            fg=TEXT_COLOR, bg=BG_COLOR
        )
        self.status_label.pack(side=tk.LEFT, padx=(6, 0))

        self.port_label = tk.Label(
            status_frame, text="", font=("Consolas", 11),
            fg=DIM_COLOR, bg=BG_COLOR
        )
        self.port_label.pack(side=tk.RIGHT)

        # Separator
        tk.Frame(self.root, bg=DIM_COLOR, height=1).pack(fill=tk.X, padx=24, pady=12)

        # Stick visualization area
        sticks_frame = tk.Frame(self.root, bg=BG_COLOR)
        sticks_frame.pack(padx=16, pady=(0, 12))

        self.left_stick = StickCanvas(sticks_frame, "Left Stick")
        self.right_stick = StickCanvas(sticks_frame, "Right Stick")

        # Bottom bar (separator + device name / version)
        tk.Frame(self.root, bg=DIM_COLOR, height=1).pack(fill=tk.X, padx=24, pady=(0, 10))

        bottom_frame = tk.Frame(self.root, bg=BG_COLOR)
        bottom_frame.pack(fill=tk.X, padx=24, pady=(0, 14))

        self.device_label = tk.Label(
            bottom_frame, text="", font=("Consolas", 10),
            fg=DIM_COLOR, bg=BG_COLOR, anchor="w"
        )
        self.device_label.pack(side=tk.LEFT)

        tk.Label(
            bottom_frame, text=f"v{VERSION}", font=("Consolas", 10),
            fg=DIM_COLOR, bg=BG_COLOR, anchor="e"
        ).pack(side=tk.RIGHT)

    def _try_connect(self):
        port, name, description = find_dji_port()
        if port is not None:
            self.port = port
            self.port_name = name
            self.port_description = description
            self.connected = True
            self._update_status(True, name)
            self.device_label.config(text=description or "")

            # Start virtual gamepad
            self.gamepad = vg.VX360Gamepad()
            self.gamepad.reset()
            self.gamepad.update()

            # Start worker threads
            self.serial_thread = Thread(
                target=serial_read_loop,
                args=(self.port, self.stick_state, self.stop_event),
                daemon=True,
            )
            self.serial_thread.start()

            self.gamepad_thread = Thread(
                target=gamepad_update_loop,
                args=(self.gamepad, self.stick_state, self.stop_event),
                daemon=True,
            )
            self.gamepad_thread.start()
        else:
            self._update_status(False, name)
            self.device_label.config(text="")
            # Retry connection every 2 seconds
            self.root.after(2000, self._try_connect)

    def _update_status(self, connected, port_name):
        if connected:
            self.status_dot.config(fg=CONNECTED_COLOR)
            self.status_label.config(text="Connected")
            self.port_label.config(text=port_name or "")
        else:
            self.status_dot.config(fg=DISCONNECTED_COLOR)
            self.status_label.config(text="Disconnected")
            self.port_label.config(text="Searching...")

    def _poll_ui(self):
        """Update the stick canvases from shared state at ~30fps."""
        self.left_stick.update(self.stick_state[LH], self.stick_state[LV])
        self.right_stick.update(self.stick_state[RH], self.stick_state[RV])
        self.root.after(33, self._poll_ui)

    def _on_close(self):
        self.stop_event.set()
        if self.port and self.port.is_open:
            self.port.close()
        self.root.destroy()

    def run(self):
        self.root.mainloop()


if __name__ == "__main__":
    App().run()

# DJI RC-N1 Virtual Gamepad Bridge

Connects a DJI RC-N1 (or RC231) controller to Windows as a virtual Xbox 360 gamepad via USB serial.

## Requirements

- Python 3.9+
- [ViGEmBus driver](https://github.com/nefarius/ViGEmBus/releases)
- [DJI Assistant 2 (Consumer Drones)](https://www.dji.com/downloads/softwares/dji-assistant-2-consumer-drones-series) - install then close (provides USB drivers)

## Install

```
pip install pyserial vgamepad
```

## Usage

1. Power on the RC-N1
2. Connect to PC via the **bottom** USB-C port
3. Run `python main.py` (or `start.bat`)
4. Launch your simulator

The script auto-detects the correct COM port. Camera wheel left/right maps to Y/B buttons (restart race / recover drone in DCL).

## Troubleshooting

- Make sure you're using the **bottom** Type-C connector, not the top one
- The script looks for a serial port with description "DJI USB VCOM For Protocol"
- If no port is found, check Device Manager for the DJI COM ports (DJI Assistant drivers must be installed)

## Compatible Controllers

- DJI Mavic 3 RC231 (confirmed)
- RC-N1 variants

For other DJI controllers see:
- [miniDjiController](https://github.com/justin97530/miniDjiController) (Mavic Mini)
- [DjiMini2RCasJoystick](https://github.com/usatenko/DjiMini2RCasJoystick) (Mini 2)
- [mDjiController](https://github.com/mishavoloshchuk/mDjiController) (Phantom 3)

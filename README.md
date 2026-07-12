# DJI RC-N1 Virtual Gamepad Bridge

Connects a DJI RC-N1 controller to Windows as a virtual Xbox 360 gamepad via USB serial. Updated to work specifically with the **DJI Mini 2** for use with the [Cyberpunk 2077 FPV mod](https://www.nexusmods.com/cyberpunk2077/mods/17830).

## Requirements

- Python 3.9+
- [DJI Assistant 2 (Consumer Drones)](https://www.dji.com/downloads/softwares/dji-assistant-2-consumer-drones-series) - install then close (provides USB drivers)

## Install

```
pip install pyserial vgamepad
```

`vgamepad` will automatically install the [ViGEmBus driver](https://github.com/nefarius/ViGEmBus/releases) (Nefarius Virtual Gamepad Emulation Bus) during setup.

## Usage

1. Power on the RC-N1
2. Connect to PC via the **bottom** USB-C port
3. Run `python main.py` (or `start.bat`)
4. Launch your simulator

The script auto-detects the correct COM port.

## Troubleshooting

- Make sure you're using the **bottom** Type-C connector, not the top one
- The script looks for a serial port with description "DJI USB VCOM For Protocol"
- If no port is found, check Device Manager for the DJI COM ports (DJI Assistant drivers must be installed)

## Compatible Controllers
- RC-N1 Variants

## Credits

This project is a fork of [DJI_RC-N1_SIMULATOR_FLY_DCL](https://github.com/IvanYaky/DJI_RC-N1_SIMULATOR_FLY_DCL) by [IvanYaky](https://github.com/IvanYaky). The original work provided the foundation for serial communication with the DJI RC-N1 controller.

If you find this project useful, please consider donating to the original author to support their work:
- GitHub: [https://github.com/IvanYaky](https://github.com/IvanYaky)

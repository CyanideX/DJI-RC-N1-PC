# RC-N1 Bridge

![DJI RC-N1 remote controller](DJI-RC-N1-Remote-Controller.png)

Use a DJI RC-N1 remote as an Xbox controller on Windows. Built for the [Cyberpunk 2077 FPV mod](https://www.nexusmods.com/cyberpunk2077/mods/17830), and works with any game or simulator that supports Xbox controllers.

The app talks to the remote over USB and feeds a virtual Xbox 360 controller, so games see an ordinary gamepad.

## What it does

- Both sticks, at about 130 updates a second.
- Reads the gimbal dial, the C/N/S flight mode switch and the Fn, Photo/Video, RTH and Capture buttons, shown live on the Home page. Sending them to games is coming with the Mapping page.
- Centres the sticks within a quarter of a second if the cable comes out or the remote goes quiet, so a drone never keeps flying on its own.
- Reconnects by itself when you plug the remote back in.
- Runs in the system tray, can start with Windows, and follows the Windows light or dark theme.

## Requirements

- Windows 10 (version 2004 or newer) or Windows 11, 64-bit.
- The [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0). If it's missing, Windows offers the download the first time you open the app.
- The [ViGEmBus driver](https://github.com/nefarius/ViGEmBus/releases/latest), which creates the virtual Xbox controller.
- The DJI USB driver. If the remote isn't found, install [DJI Assistant 2 (Consumer Drones Series)](https://www.dji.com/downloads/softwares/dji-assistant-2-consumer-drones-series) once, then close it.

## Getting started

1. Install ViGEmBus.
2. Unzip the release and run `RCN1Bridge.exe`. It's a single file, so you can keep it anywhere.
3. Plug the cable into the **bottom** USB-C port on the remote, then power the remote on.
4. The Home page turns green and shows "Connected". Move the sticks and you'll see them move on screen.
5. Start your game.

Closing the window keeps the bridge running in the tray. Right-click the tray icon to quit, or turn that off in Settings.

## Troubleshooting

The Home page shows a checklist whenever something is missing, with the fix for each step. The common ones:

| You see | Try this |
|---|---|
| Looking for the controller | Use the bottom USB-C port. The top port only charges a phone. Then power the remote on. |
| COMx is in use by another program | Close DJI Assistant, the old Python bridge, or anything else using the remote. |
| Waiting for stick data | Unplug the remote, plug it back in, then power it on. |
| Virtual controller driver missing | Install ViGEmBus, then click **Check again**. |
| Stick data stopped | Check the cable and that the remote is still on. The sticks stay centred until it answers again. |

Still stuck? Open **Diagnostics**, click **Copy diagnostics**, and paste the result into an issue. It includes the connection state, the COM ports Windows sees, the raw messages from the remote and the recent log.

## Where it keeps things

- Settings: `%APPDATA%\RCN1Bridge`
- Logs: `%LOCALAPPDATA%\RCN1Bridge\logs` (kept for 7 days)
- Start with Windows adds an `RCN1Bridge` entry under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. Turning it off removes the entry.

The app never asks for admin rights, never downloads anything and sends no data anywhere.

## Building from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```
dotnet build RCN1Bridge.slnx -c Release
dotnet test RCN1Bridge.slnx -c Release
```

To make the single `RCN1Bridge.exe` in `publish\`:

```
dotnet publish src/RCN1Bridge.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o publish
```

Project layout:

- `src/RCN1Bridge.Core` holds the protocol, the connection engine and stick processing, with no UI code.
- `src/RCN1Bridge.App` is the WPF app.
- `tests/RCN1Bridge.Core.Tests` has the unit tests, including connection tests against a simulated remote.
- `tools/PollBench` measures stick and button update rates on a real remote. Close the app first, then run `dotnet run --project tools/PollBench -c Release`.

## The Python version

The original Python bridge (`main.py`, `ui.py`) lives on the [`legacy`](../../tree/legacy) branch.

## Credits

This project started as a fork of [DJI_RC-N1_SIMULATOR_FLY_DCL](https://github.com/IvanYaky/DJI_RC-N1_SIMULATOR_FLY_DCL) by [IvanYaky](https://github.com/IvanYaky), which worked out the serial connection to the RC-N1. If you find this useful, please consider supporting the original author.

- DUML command names and message layouts from the dissectors in [dji-firmware-tools](https://github.com/o-gs/dji-firmware-tools).
- The button and flight mode request (command 06/27) was first documented by [RC-N1_ControllerBridge](https://github.com/Samukashvili/RC-N1_ControllerBridge).
- [ViGEmBus](https://github.com/nefarius/ViGEmBus) and [Nefarius.ViGEm.Client](https://github.com/nefarius/ViGEm.NET) for the virtual controller.
- [WPF-UI](https://github.com/lepoco/wpfui) for the Windows 11 look.

## License

[Apache 2.0](LICENSE)

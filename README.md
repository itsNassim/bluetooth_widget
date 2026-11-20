# Bluetooth Battery Tray

Lightweight Windows 10/11 tray app showing the battery level of connected Bluetooth devices.

## Build locally
```
dotnet publish -c Release -o publish
```

## Pipeline (GitHub Actions)
- Every push / PR: builds the app, the installer (Inno Setup) and a portable zip, uploaded as workflow artifacts.
- Push a tag to publish a GitHub Release with both files attached:
```
git tag v1.0.0
git push origin v1.0.0
```

Outputs: `BtBatteryTray-<version>-setup.exe` (per-user installer, no admin needed) and `BtBatteryTray-<version>-portable-win-x64.zip`.

## Supported sources
- Bluetooth (classic + BLE) devices that report a battery level to Windows
- XInput gamepads (coarse levels, only if the controller reports its battery)
- Tray menu > "Save device report" writes `hid-dump.txt` (all HID devices + raw XInput values) to the Desktop,
  used to add support for new 2.4 GHz dongle devices (implement `IBatteryReader` in `Readers/`).
- Tray menu > "Listen to vendor interfaces" writes `hid-listen.txt` (read-only probe of vendor-defined HID interfaces).

## Finding the battery protocol of a 2.4 GHz device
`tools/battery-capture.ps1` (run as Administrator, needs USBPcap) captures USB traffic while the official software
shows the battery, then searches the capture for the request/reply containing that value:
```
powershell -ExecutionPolicy Bypass -File tools\battery-capture.ps1 -Seconds 45 -VendorId 3537
```

## Supported sources
| Source | Devices | Notes |
|---|---|---|
| Bluetooth | any paired device that reports a battery to Windows | updates on connect / disconnect |
| XInput | Xbox-style pads | 4 coarse levels, only if the pad reports a real battery |
| GameSir | Nova 2 Lite on its 2.4 GHz dongle (3537:1098) | queries the dongle (3 small packets) every 30 s; battery = byte 18 of the reply |
| Attack Shark | X6 / X11 / other Xenta (1D57) mice on the 2.4 GHz dongle | 100 % passive: the dongle pushes a frame every 2 s (`03 MM 40 ST NN`); also gives charging state |
| Logitech | mice/keyboards on a Unifying, Lightspeed or Bolt receiver (046D), HID++ 1.0 and 2.0 | **UNCONFIRMED**, needs testing on real hardware |
| Razer | mice on a 2.4 GHz receiver (1532), standard 90-byte feature report | a handful of models in a table in `RazerReader.cs`; **UNCONFIRMED**, needs testing |

Connect / disconnect is detected automatically (USB hot-plug events, Bluetooth watcher, mouse frames).
Charging: Attack Shark = known (state byte). GameSir = not known yet (needs a capture while charging).

## Taskbar widget ("Minimal bars" style)
Docked left of the system tray, in one rounded violet capsule: per device a glyph (mouse, gamepad, headset, keyboard)
with a thin level bar underneath (red <= 20 %, orange <= 50 %, green above) and a small bolt while charging.
The percentage ("47%") is shown next to each icon (menu: "Show percentage next to icons" to hide it). Right-click: theme, move left/right, tray icon on/off.

## Adding a device
Implement `IBatteryReader` in `Readers/` and add it to the list in `TrayApp`. Tray menu > "Save device report" and
`tools/battery-capture.ps1` help to find the protocol of a new 2.4 GHz device.

## Credits / references (protocols)
- Attack Shark battery frame: daeoxide/attackshark-battery-windows, gabrielmf1998/Attack-Shark-Battery-KDE, blak0p/attack-shark-linux (all MIT or similar)
- Multi-brand mouse battery drivers worth porting (Logitech HID++, Razer, Lamzu, Compx...): Fan4Metal/mouse_tray

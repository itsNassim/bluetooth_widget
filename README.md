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

## GameSir Nova 2 Lite (2.4 GHz dongle)
Experimental reader (tray menu > "GameSir Nova 2 Lite via dongle"), opt-in. It sends the same 3 packets the official
app sends (handshake, info query, handshake) and reads the battery from byte 18 of the reply. Derived from one capture
at 82%: confirm it with a second capture at a different level before trusting it.

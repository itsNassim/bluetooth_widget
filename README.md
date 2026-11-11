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

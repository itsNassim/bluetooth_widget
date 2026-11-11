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

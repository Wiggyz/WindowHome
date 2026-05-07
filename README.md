# WindowHome

WindowHome is a Windows 11 desktop app that watches for configured app windows and moves them to saved monitor layouts.

## Features

- Detects active monitors
- Saves app-to-monitor rules
- Saves exact window position and size
- Saves maximized placement per monitor
- Moves matching windows automatically when they open
- Uses Windows event hooks instead of background polling for app detection
- Optional tray mode
- Optional Windows autostart

## Build

```powershell
dotnet build
```

## Publish single exe

```powershell
dotnet publish .\MonitorApp.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

## Download

Built exe is in `dist/WindowHome.exe` until GitHub Release upload is configured.

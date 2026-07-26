# Conduit — Windows app

C# / .NET 9 / WPF. Connects to the Android app over your local WiFi and provides
file/clipboard transfer, notification mirroring, media/remote control, and a phone dashboard.

## Requirements

- .NET 9 SDK (`dotnet --version` ≥ 9.0).
- Windows 10/11.
- On the **same WiFi network** as the phone.

## Build & run

```bash
cd windows
dotnet build
dotnet run --project src/Conduit.App
```

Or open `Conduit.sln` in Visual Studio 2022 and press **F5**.

The app starts minimized to the system tray. Double-click the tray icon to open the window.

> If Windows Firewall prompts on first run, **allow** Conduit on private networks — it needs
> UDP 5461 (discovery) and TCP 5462 (session).

## Projects

| Project        | What it is |
|----------------|------------|
| `Conduit.Core` | Protocol, networking, ECDH/AES crypto, discovery, logging. No UI — reusable engine. |
| `Conduit.App`  | WPF UI, system tray, and Windows-specific feature services (clipboard, media keys, power, files, notifications). |

```
src/
├── Conduit.Core/
│   ├── Protocol/Packet.cs           envelope + packet types (matches Android + PROTOCOL.md)
│   ├── Networking/                  FrameCodec, DeviceDiscovery, PeerConnection, ConduitNode
│   ├── Security/CryptoService.cs    ECDH P-256 + AES-256-GCM
│   ├── Storage/AppStore.cs          identity, settings, paired devices (JSON in LocalAppData)
│   ├── Models/DeviceModels.cs
│   └── Logging/ConduitLog.cs        Serilog → rolling file + console
└── Conduit.App/
    ├── App.xaml(.cs)                startup, tray, wiring
    ├── MainWindow.xaml(.cs)         device list, dashboard, actions, logs
    ├── ViewModels/MainViewModel.cs
    └── Services/                    Clipboard, Media, Power, FileTransfer, Notification, FeatureCoordinator
```

## Logs

Written to `%LOCALAPPDATA%\Conduit\logs\conduit-<date>.log` (rolls daily, keeps 14 days).
Open them from the **Logs** tab in the app, the tray menu (*Open logs folder*), or directly.
Every subsystem logs with a tag: `[Discovery]`, `[Connection]`, `[Node]`, `[Features]`,
`[FileTransfer]`, `[Clipboard]`, `[Media]`, `[Notifications]`, etc.

## Config

`%LOCALAPPDATA%\Conduit\config.json` holds this PC's device id, name, key pair, paired
devices, and the download folder (default `%USERPROFILE%\Downloads\Conduit`).

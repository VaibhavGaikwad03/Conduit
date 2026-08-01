# Conduit

**Conduit** is a full-fledged Android ↔ Windows ecosystem that connects your phone and PC seamlessly over your local WiFi network — no cloud, no accounts, no data leaving your network.

Think of it as an open, self-hosted alternative to *Phone Link* / *KDE Connect*.

## Repository layout

```
Conduit/
├── README.md            ← you are here
├── PROTOCOL.md          ← the shared wire protocol (the "contract" both apps implement)
├── windows/             ← Windows desktop app (C# / .NET 9 / WPF)
│   ├── Conduit.sln
│   └── src/
│       ├── Conduit.Core/     ← protocol, networking, crypto, logging (no UI)
│       └── Conduit.App/      ← WPF UI, system tray, Windows-specific feature services
└── android/             ← Android app (Kotlin / Jetpack Compose)
    └── app/src/main/java/io/conduit/
```

## Features

| Feature                     | Direction        | Windows | Android |
|-----------------------------|------------------|:-------:|:-------:|
| Device auto-discovery       | —                |   ✅    |   ✅    |
| Secure pairing              | —                |   ✅    |   ✅    |
| File transfer               | both ways        |   ✅    |   ✅    |
| Clipboard sync              | both ways        |   ✅    |   ✅    |
| Notification mirroring      | Android → Windows|   ✅    |   ✅    |
| Notification reply/dismiss  | Windows → Android|   ✅    |   ✅    |
| Media / remote control      | both ways        |   ✅    |   ✅    |
| Battery & device status     | Android → Windows|   ✅    |   ✅    |
| SMS list / send             | both ways        |   ✅    |   ✅    |
| Cross-device file search    | both ways        |   ✅    |   ✅    |
| Open link on other device   | both ways        |   ✅    |   ✅    |
| Phone as PC webcam          | Android → Windows|   ✅    |   ✅    |
| Screen mirroring            | Android → Windows|   ✅    |   ✅    |
| Remote control (touch/type) | Windows → Android|   ✅    |   ✅    |

### Phone permissions some features need (granted once)

A few features rely on Android permissions the user enables once on the phone:

- **Notification mirroring** — *Notification access* for Conduit (Settings → Notification access).
- **Screen mirroring** — the system screen-capture consent, prompted each time the PC starts it.
- **Remote control** (touch/type from the PC) — the *Conduit Remote Control* accessibility service
  (Settings → Accessibility). Reinstalling the app disables it, so re-enable after an update.
- **Phone as webcam** — the Camera permission.

## How the connection works (seamless by design)

1. **Discovery** — every device broadcasts a small UDP "identity" beacon on the LAN
   (port `5461`). Both apps listen and build a live list of nearby devices.
2. **Pairing** — the first time two devices meet, they exchange public keys and show a
   matching 6-digit code. Once confirmed, the peer is remembered (trusted store).
3. **Session** — a persistent, encrypted TCP connection (port `5462`) carries all
   feature traffic using the length-prefixed JSON protocol in [`PROTOCOL.md`](PROTOCOL.md).
4. **Heartbeat** — periodic `ping`/`pong` keeps the link alive and detects drops so the
   UI reflects connection state instantly.

## Logging (built in on both sides)

Both apps write structured, rotating logs so you can diagnose runtime bugs without a debugger.

- **Windows:** `%LOCALAPPDATA%\Conduit\logs\conduit-<date>.log` (Serilog, rolling daily).
- **Android:** `<app files>/logs/conduit-<date>.log` (Timber + rotating file tree),
  also visible via `adb logcat -s Conduit`.

Every subsystem logs with a tag (`Discovery`, `Connection`, `Clipboard`, `FileTransfer`,
`Notifications`, `Media`, `Pairing`) and a level. Set the level in each app's settings.

## Build & run

See [`windows/README.md`](windows/README.md) and [`android/README.md`](android/README.md).

Quick start:

```bash
# Windows
cd windows
dotnet build

# Android
cd android
./gradlew assembleDebug
```

## Security notes

- All session traffic is encrypted (ECDH P-256 key exchange → AES-256-GCM).
- Devices must be paired (6-digit code confirmation) before any feature works.
- Nothing is sent off the LAN; there is no server and no telemetry.

# Conduit — Android app

Kotlin + Jetpack Compose. Connects to the Windows app over your local WiFi and provides
file/clipboard transfer, notification mirroring, media/remote control, and device status.

## Requirements

- Android Studio (Ladybug or newer) **or** JDK 17 + Android SDK (platforms 34/35, build-tools 35).
- A phone/emulator on **the same WiFi network** as the PC.

## Build

From Android Studio: open the `android/` folder and press **Run**.

From the command line:

```bash
cd android
./gradlew :app:assembleDebug
```

The APK lands in `app/build/outputs/apk/debug/app-debug.apk`. Install it:

```bash
adb install -r app/build/outputs/apk/debug/app-debug.apk
```

> **Note on this machine:** this PC has a documented loopback/Selector fault that the
> global `~/.gradle/gradle.properties` already works around (`-Djdk.net.unixdomain.tmpdir=C:\Temp`),
> which is why Android Studio builds succeed. The same arg is mirrored into
> `gradle.properties` so `./gradlew` works from a normal terminal too. Drop it on other machines.

## First run — grant access

Conduit needs a few permissions for the features to work. The app requests the runtime ones
on launch; two must be granted in system Settings:

1. **Notification access** (for notification mirroring): the app has an *"Enable notif mirroring"*
   button that opens *Settings → Notification access → Conduit*.
2. **Location** (Android shows Wi-Fi SSID only with location granted).
3. **SMS** (for the messaging feature) — optional.

Then pair with your PC from the device list and you're connected.

## Project structure

```
app/src/main/java/io/conduit/
├── ConduitApp.kt              app entry; initializes logging
├── protocol/Packet.kt         wire protocol (matches Windows + PROTOCOL.md)
├── logging/                   Timber + rotating file logger
├── network/                   Crypto, FrameCodec, Discovery, PeerConnection, ConduitNode
├── model/Device.kt            device + ports model
├── storage/AppStore.kt        identity, settings, paired devices (SharedPreferences)
├── runtime/ConduitRuntime.kt  process-wide handle for the UI
├── service/ConduitService.kt  foreground service that owns the node
├── features/                  clipboard, media, file, battery, status, remote, sms, notifications
└── ui/                        Compose UI (MainActivity, Theme)
```

## Logs

On-device logs are written to `<app files>/logs/conduit-<date>.log` and shown in the app's
**View logs** panel. Live logs: `adb logcat -s Conduit` (all tags are prefixed `Conduit/`).

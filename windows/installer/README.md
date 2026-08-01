# Conduit — Windows installer

Builds a single `ConduitSetup-<version>.exe` that installs Conduit on **any 64-bit
Windows 10/11 PC** — the target machine does **not** need .NET installed (the app is
published self-contained).

## Build it

From the `windows` folder:

```powershell
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
```

Pass a version if you like:

```powershell
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 -Version 1.1.0
```

The finished installer lands in:

```
windows\artifacts\installer\ConduitSetup-<version>.exe
```

Copy that one `.exe` to any Windows PC and run it — nothing else needed.

## What the build does

1. `dotnet publish -r win-x64 --self-contained true` → `windows\artifacts\publish`
   (the whole .NET runtime + `ConduitCamera.dll` bundled).
2. Compiles `Conduit.iss` with **Inno Setup** into a single compressed setup `.exe`.

## What the installer does on the target PC

- Installs to `C:\Program Files\Conduit`.
- Creates a Start Menu shortcut (and an optional Desktop shortcut).
- Adds a **Windows Firewall** rule so Conduit can reach the phone over the LAN
  (UDP 5461 discovery, TCP 5462 session — private/domain networks).
- Optional: start Conduit automatically at sign-in.
- Registers a proper **uninstaller** (Apps & features) that removes the app, the
  firewall rule, and unregisters the virtual-camera DLL.

> The "phone as webcam" feature still triggers a one-time UAC prompt the first time
> it's used — that's the app registering the virtual camera with Windows, by design.

## Prerequisites (build machine only)

- .NET 9 SDK
- Inno Setup 6 — install once with:
  ```powershell
  winget install JRSoftware.InnoSetup
  ```

## Notes

- Output is x64. For ARM64 Windows, build with `-Runtime win-arm64` (also update
  `ArchitecturesAllowed` in `Conduit.iss`).
- The installer is **unsigned**, so SmartScreen may show a "Windows protected your
  PC" warning on first run — click **More info → Run anyway**. To remove that, sign
  `ConduitSetup-*.exe` with a code-signing certificate.

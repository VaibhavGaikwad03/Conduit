# Running Conduit (dev) — app + locked-control agent

Quick commands for the local test loop. Paths assume the repo lives at
`C:\Users\Vaibhav Gaikwad\Desktop\Projects using AI\Conduit`.

> Two shells matter here:
> - **Elevated** PowerShell ("Run as administrator") — for anything touching the service or `C:\ProgramData`.
> - **Normal** PowerShell — for running the app (it must sit in your user session).

---

## 1. Build

```powershell
cd "C:\Users\Vaibhav Gaikwad\Desktop\Projects using AI\Conduit\windows"
dotnet build Conduit.sln -c Release
```

Builds the app, core, **ConduitAgent** (service) and **ConduitHelper**.
If the app is running it locks its DLLs — close it first (see below).

Native capture DLL (only when the C++ under `windows/native` changed):

```powershell
& "C:\Users\Vaibhav Gaikwad\Desktop\Projects using AI\Conduit\windows\native\build.ps1"
```

---

## 2. Install / start the agent service  *(elevated, one-time)*

Copies the agent **and** helper into one folder, registers the LocalSystem service, and starts it.

```powershell
$src  = "C:\Users\Vaibhav Gaikwad\Desktop\Projects using AI\Conduit\windows\src"
$dest = "C:\ProgramData\Conduit\agent"
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item "$src\Conduit.Agent\bin\Release\net9.0-windows\*"  $dest -Recurse -Force
Copy-Item "$src\Conduit.Helper\bin\Release\net9.0-windows\*" $dest -Recurse -Force
& "$dest\ConduitAgent.exe" --install     # sc create + start
Get-Service ConduitAgent
```

Start / stop the already-installed service *(elevated)*:

```powershell
Start-Service ConduitAgent
Stop-Service  ConduitAgent
```

---

## 3. Run the app  *(normal shell)*

Set the flag that routes the PC-desktop mirror through the agent (the locked-control path),
then launch:

```powershell
Get-Process Conduit -ErrorAction SilentlyContinue | Stop-Process -Force
$env:CONDUIT_DESKTOP_AGENT = "1"
& "C:\Users\Vaibhav Gaikwad\Desktop\Projects using AI\Conduit\windows\src\Conduit.App\bin\Release\net9.0-windows\Conduit.exe"
```

Leave out the `$env:CONDUIT_DESKTOP_AGENT` line to use the normal in-process mirror
(no agent involved).

On the phone: open the PC → **Mirror & control PC**. When the agent path is live a
`ConduitHelper.exe` appears while the mirror runs.

---

## 4. Redeploy just the helper after a code change  *(elevated)*

The agent launches a fresh helper each time you open the mirror, so no service restart is
needed — close the mirror on the phone first so the old helper exits, then:

```powershell
Get-Process ConduitHelper -ErrorAction SilentlyContinue | Stop-Process -Force
Copy-Item "C:\Users\Vaibhav Gaikwad\Desktop\Projects using AI\Conduit\windows\src\Conduit.Helper\bin\Release\net9.0-windows\*" "C:\ProgramData\Conduit\agent" -Recurse -Force
```

Re-open the mirror on the phone to pick it up.

---

## 5. Status & logs

```powershell
Get-Service ConduitAgent
Get-Process ConduitHelper -ErrorAction SilentlyContinue | Select-Object Id, ProcessName
```

- **App + helper log** (runs as you): `%LOCALAPPDATA%\Conduit\logs\conduit-*.log`
  — tags `[Desktop]`, `[Helper]`, `[HelperInput]`.
- **Agent log** (runs as SYSTEM): `C:\Windows\System32\config\systemprofile\AppData\Local\Conduit\logs\`.

Tail the user log:

```powershell
$log = Get-ChildItem "$env:LOCALAPPDATA\Conduit\logs\conduit-*.log" | Sort-Object LastWriteTime | Select-Object -Last 1
Get-Content $log.FullName -Wait -Tail 20
```

---

## 6. Uninstall the service  *(elevated)*

```powershell
& "C:\ProgramData\Conduit\agent\ConduitAgent.exe" --uninstall   # sc stop + delete
```

---

### Notes
- The agent must run as **LocalSystem** — that privilege is what lets it launch the helper onto
  another desktop (Stage 1 uses the ordinary desktop; the lock screen comes later).
- The app is the only crypto/pairing endpoint; the agent + helper only move opaque frames and a
  fixed input struct over a local, ACL'd named pipe. See the plan / `PROTOCOL.md`.

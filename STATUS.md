# Conduit — work in progress

Snapshot of where the "view & control the PC from the phone" work stands, so it can be resumed.
Dev run/redeploy commands live in [RUNNING.md](RUNNING.md). Full design in the plan file
(`~/.claude/plans/shiny-chasing-aho.md`) and `PROTOCOL.md`.

## 1. Base feature — DONE, committed & pushed (commit `5cb15cf`)

**View & control the PC desktop from the phone** (unlocked only). The reverse of the phone→PC
mirror: PC captures its primary display (DXGI Desktop Duplication + MF H.264 encoder, cursor
composited), streams H.264 to the phone on TCP **5466**; phone decodes to a full-screen surface and
sends **direct-touch** control back as absolute `pc-input` (tap = click there, drag, long-press =
right-click, two-finger scroll, soft keyboard).

Key files: `windows/native/ConduitCamera/DesktopCapture.cpp`, `Services/DesktopShareService.cs`,
`Services/InputService.cs` (absolute actions), `Services/FeatureCoordinator.cs` (dispatch),
`android/.../ui/DesktopMirrorActivity.kt`, `MainActivity.kt` ("Mirror & control PC" button).
Verified working end-to-end on the vivo device.

## 2. Locked-PC control — IN PROGRESS (all UNCOMMITTED)

Goal: view/control the PC while Windows is **locked**. A user-session app can't capture or inject on
the secure desktop, so a **LocalSystem service** launches a **helper** onto the target desktop.
Security model (approved): **off by default + per-device "locked access" grant + Conduit PIN**.

Staged build — **Stages 1 & 2 code-complete (built clean, awaiting one live retest each); Stage 3 remains.**

### Stage 1 — service + helper + IPC on the UNLOCKED desktop  ← we are here
Proves the launch/capture/relay plumbing. **Working**, one fix pending retest.

New pieces (all uncommitted):
- `windows/src/Conduit.Core/Agent/AgentIpc.cs` — pipe framing + fixed binary `InputMsg` (no JSON on the SYSTEM side).
- `windows/src/Conduit.Agent/` — new LocalSystem service project. `PipeServer` (ACL'd app pipe),
  `HelperSession` (launch + relay), `HelperLauncher` (`CreateProcessAsUser` onto a desktop),
  `ServiceInstaller` (`--install`/`--uninstall`), `AgentService`, `AgentPipe` (shared pipe DACL).
- `windows/src/Conduit.Helper/` — new desktop-bound worker. `NativeDesktop` (capture P/Invoke),
  `Win32Input` (SendInput), `Program`.
- `windows/src/Conduit.App/Services/AgentDesktopShare.cs` — app-side agent path (frames→phone, input→agent).
- `FeatureCoordinator.cs` — routes the mirror via the agent when env var `CONDUIT_DESKTOP_AGENT=1`
  is set; forwards `pc-input` to the agent while that session is live (`ToInputMsg`).
- `Conduit.sln` — added the two projects.

Verified live: service installs & runs as LocalSystem; `desktop-start` from the phone makes the
agent launch `ConduitHelper.exe`; frames reach the phone; input drives the cursor. No errors in logs.

**Last change — awaiting build + retest (the reason we paused):**
- Symptom: taps did nothing on **elevated** apps (Task Manager, elevated PowerShell, elevated
  Conduit). Cause: helper launched with the **filtered medium-integrity token** → UIPI blocks input
  into high-integrity windows.
- Fix (written, NOT yet built/deployed): `HelperLauncher.Launch` now prefers the user's **elevated
  linked token** (`TryGetLinkedToken`, `TokenLinkedToken`) so the helper runs high-integrity. Logs
  `Helper token: elevated (linked)`.
- Also done in this batch: `Win32Input` gives synthetic clicks a 30 ms dwell + logs each tap/click
  (helper log tag `[HelperInput]`); `AgentDesktopShare.Input` failure log bumped to Debug.

**➡️ To resume Stage 1:** build the agent (`dotnet build …\Conduit.Agent\Conduit.Agent.csproj -c
Release`), redeploy agent+helper to `C:\ProgramData\Conduit\agent` and restart `ConduitAgent` (see
RUNNING.md §2/§4), retest taps on Task Manager/PowerShell. If good → **commit Stage 1** (it's all
uncommitted). Note: elevated helper still can't touch the UAC consent dialog — that's Stage 2.

### Stage 2 — the secure desktop (actual lock-screen control) — CODE DONE, awaiting live test
Implemented as a **self-retargeting agent path**: the app keeps its stable socket+pipe while the agent
swaps the helper "leg" underneath it across desktop switches, so the phone mirrors straight through a
lock/unlock. No app-side in-process↔agent swap was needed (the agent path covers both desktops).

New/changed pieces (all uncommitted, built clean):
- `HelperLauncher.Launch(pipeName, desktop, secure, log)` — new **secure** path launches the helper as
  SYSTEM (clone the agent's own token, pin it to the console session) onto `WinSta0\Winlogon`, whose
  DACL admits only SYSTEM. The Stage-1 user-linked-token path stays for `WinSta0\Default`.
- `AgentDesktops.cs` (new) — `DesktopTarget` (`Interactive` / `SecureDesktop`) + `SessionState`
  (`IsConsoleLocked` via `WTSQuerySessionInformation`/WTSINFOEX, so a mirror opened while already
  locked starts on the secure desktop).
- `HelperSession` — now retargetable: `Retarget(target)` tears down the current leg (helper proc +
  pipe + relay thread, joined) and relaunches on the new desktop, keeping the app stream continuous.
- `PipeServer` — tracks the live session + lock state; `OnDesktopLocked/Unlocked` retarget it.
- `AgentService` — `CanHandleSessionChangeEvent = true`; `OnSessionChange` maps SessionLock →
  secure desktop, SessionUnlock → interactive desktop. `OnShutdown` added.

**➡️ To validate Stage 2:** redeploy agent+helper + restart service (RUNNING.md §2/§4 — note Stage 2
needs a **service restart**, not just a helper copy, since the launcher/service changed). Open the
mirror on the phone (unlocked), then press Win+L. Expected: the phone keeps mirroring and shows the
lock screen; typing the PIN via the phone unlocks. Agent log shows `Console locked` →
`Retargeting helper WinSta0\Default -> WinSta0\Winlogon` → `Helper token: SYSTEM`.

Known limitation (Stage 2.1): the **UAC** secure desktop while *unlocked* isn't auto-followed — the
SCM only raises lock/unlock, not UAC-desktop switches. Catching that needs the helper to report
capture-loss (DXGI `ACCESS_LOST`) so the agent re-evaluates; deferred. DXGI Desktop Duplication may
also refuse to capture the UAC prompt even as SYSTEM (an OS restriction), so input still routes but the
frame can be black there.

### Stage 3 — security gating — NOT STARTED
Off-by-default setting; per-device "locked access" grant in the paired-peer store; Conduit PIN
(hashed on PC, prompted on phone); audit logging; capability/status bits in `PROTOCOL.md` + Android
PIN UI. **Run `/security-review`** on the SYSTEM service, IPC, and token/PIN handling before shipping.

## Uncommitted files (to commit when Stages 1 & 2 are confirmed live)
`Conduit.Core/Agent/AgentIpc.cs`, all of `Conduit.Agent/` (incl. new `AgentDesktops.cs`) and
`Conduit.Helper/`, `Conduit.sln`, `Conduit.App/Services/AgentDesktopShare.cs`,
`Conduit.App/Services/FeatureCoordinator.cs`, `RUNNING.md`, `STATUS.md`.

# Bansa

A non-intrusive, fully-reversible per-app bandwidth **and hardware** monitor for Windows — all network monitoring and control is built on the OS's own machinery (no kernel drivers), so every change can be undone with one click. The one exception is hardware sensor access — see [the note below](#the-one-exception-the-hardware-sensor-driver).

> **Status:** v1.3 — personal use, no commercial intent.

**New in v1.3 — Network Tools.** A new Network-only **Network Tools** panel: choose which connection Windows prefers (interface priority — system-wide but fully reversible to Automatic or your previous metrics); launch a native app with its traffic forced out one connection via ForceBindIP (one app over Wi-Fi, another over Ethernet), with up-front warnings for Microsoft Store and Electron/Chromium apps that can't be bound; and an on-demand connection scan that shows which connection each running app is actually using and flags any on the wrong one.

**v1.2 — robustness & efficiency.** Crash-safe settings (atomic write + automatic backup recovery), single-instance guard, a manual update check, monthly usage tracking with an optional data budget, multi-GPU picker, a 7d/30d history fix, Limits & Scenarios consolidation, visibility-gated rendering (near-zero UI cost while in the tray), SpaceSniffer in Tools, a unit-test suite, and CI.

**v1.1 — dual-domain redesign.** A header toggle switches Bansa between two purposes, re-skinning the whole accent:
> - **Network** — live throughput, per-app traffic, limits & scenarios.
> - **Hardware** — CPU/GPU thermal gauges, an overlaid temperature timeline, and recordable monitoring sessions (great for diagnosing a friend's PC).
>
> Plus a UniFi-style mirrored throughput chart on the dashboard, a redesigned floating HUD (Net / Temp / Both), and a split History (Network vs Hardware).

---

## Installation

1. Download `Bansa.zip` from the [Releases](../../releases/latest) page.
2. Extract it — a `Bansa\` folder is created containing `Bansa.exe`.
3. Run `Bansa.exe` as Administrator (UAC prompt will appear — required for ETW and firewall access).

That's it. No installer, no setup wizard.

**Requirements:** [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) must be installed on the target machine.

### Where data lives

```
Bansa\
├── Bansa.exe          ← the app
└── Data\
    └── Tools\         ← portable tools you've placed here (opened from the Tools tab)

%LocalAppData%\Bansa\
├── settings.json      ← all preferences (export/import via Settings → General)
├── bansa.db           ← bandwidth history (SQLite)
├── sessions\          ← recorded hardware-monitoring sessions (one JSON per session)
└── crash.log          ← written only on unhandled exceptions
```

Settings and history live in `%LocalAppData%\Bansa\` so they survive updates and are per-user.
Tool executables live next to `Bansa.exe` in `Data\Tools\` so they stay portable with the app folder.

---

## Updating

Replace `Bansa.exe` with the new version. Settings, history, and any tools in `Data\Tools\` are untouched.

---

## What it does

**Live monitoring**
- App list grouped by name (all `chrome.exe` PIDs collapse into one row). Double-click any app to see its individual processes and connections.
- Per-app download rate, upload rate, total bytes, active connection count.
- Tray icon with live ↓/↑ rates and a ping indicator. Left-click to toggle the popup; double-click to open the main window. Popup position is remembered when dragged.
- Continuous ping to a configurable target displayed in the sidebar.
- Sidebar STATUS cluster — live CPU/GPU temperature donuts, download/upload totals, ping, and Scenarios / Global Cap toggles. Each box is clickable and jumps to the matching tab.
- **Hardware dashboard** — CPU/GPU/RAM radial thermal gauges plus an overlaid CPU/GPU temperature timeline (smoothed, gradient-filled) with hover crosshair. On multi-GPU machines (laptop iGPU + dGPU) a picker in the GPU card chooses which GPU is shown.
- **Network dashboard** — Download / Upload / Ping hero, a UniFi-style mirrored throughput timeline (download above the axis, upload below), a per-app bandwidth-share donut, and a live top-talkers list.
- **Floating HUD** — detachable, always-on-top overlay with three tabs (**Net / Temp / Both**): network sparkline + ping + top apps, CPU/GPU temp gauges + timeline, and RAM. Drag via the rates bar or the bottom switch; right-click for options. Position is remembered and safely clamped to the visible screen on different machines.

**Filtering**
- Filter by app name.
- Threshold to hide apps below a rate (1 KB/s, 10 KB/s, 50 KB/s, or custom).
- Hide local-only apps (loopback/LAN traffic).

**Live Traffic chart**
- 30-second live sparkline with smooth Catmull-Rom curves.
- Click to pause/resume; click again to snap back to live.
- Drag right to scroll back through up to **1 hour of history**; label shows how far back you're looking.
- Chart height is remembered between launches (drag the divider between chart and app list).

**Controls (per app, all reversible)**
- **Block** — adds a Defender Firewall rule named `Bansa-Block-<app>`.
- **Upload limit** — smooth kernel-level rate shaping via Windows QoS Policy (`Bansa-Qos-<app>-limit`). No connection drops; takes effect on the app's next connection (the badge goes amber while an existing connection is still over the cap).
- **Download limit** — best-effort throttling via inbound monitor + pulsed firewall block (`Bansa-Throttle-<app>`); Windows has no smooth user-mode way to rate-limit inbound traffic.
- **Limit profiles** — named presets (e.g., "Gaming", "Backup") for quick reuse. Right-click any app → **Quick profile** to apply a profile in one step. Edit profiles in Settings → Network.

**Scenarios**
- A saved set of **per-app** upload/download limits that toggle on and off together with one click (sidebar card or Dashboard card).
- Intended to throttle background apps (Spotify, Discord, cloud sync) so they don't compete with your game. Apps not listed are unaffected.
- When you turn it off, each app's previous (pre-Scenario) limit is restored. Configure the per-app entries in the **Limits & Scenarios** tab.

**Global Upload Cap**
- A separate, system-wide outbound cap — keeps bufferbloat from degrading latency while uploading.
- **Two enforcement layers:** QoS Group Policy (zero-overhead, handles new connections) + pulsed firewall rules (catches existing connections, UDP traffic, and anything QoS misses). Set it from the **Limits & Scenarios** tab (the sidebar Global Cap button jumps there to set a value).
- An **enable/disable switch** pauses the cap without discarding the configured value, so you can drop it for a big upload and switch it back on afterward.
- Optionally **keep the cap active when Bansa is closed** — leaves the smooth QoS layer in place so the cap persists across reboots without the app running. (The firewall hard layer still needs the app open.) It's the one thing Bansa intentionally leaves behind; turning the cap off, the **Clean Up** button, or `Uninstall-Bansa.ps1` removes it.

**History tab** (domain-aware — shows Network history in Network mode, Hardware history in Hardware mode)
- *Network* — total bytes per app over any date range (Today / Last 7 d / Last 30 d / custom); a **"This month" usage tile with an optional monthly data budget** (GB, turns red when exceeded — for capped/metered connections); activity log of every block, limit, and priority change; export to CSV.
- *Hardware* — **record a monitoring session** to log CPU/GPU temperatures and loads, each tagged with the foreground app so a thermal spike can be traced to the game/app that caused it. Sessions **auto-save and persist** across restarts (a picker lists past sessions), with min/avg/max + peak-with-culprit summary, a "hottest apps" list, and CSV export.

**Tools tab**
- Browse and launch portable utilities: OpenRGB, HWiNFO, ShareX, Chris Titus WinUtil, FanControl, DDU — all with real brand logos.
- Tools stored as portables in `Data\Tools\`; click the directory link to open the folder directly.
- Website button opens the download page when a tool isn't installed yet.
- ⚠️ Bansa runs as administrator, so anything launched from this tab **inherits admin rights** — only place executables you trust in `Data\Tools\`.

**Limits & Scenarios tab**
- **Apps with limits** — every app that currently has an up/down limit, with inline **Edit** / **Clear** (no need to find it in the live list first), plus a legend explaining what the amber throttle badges mean.
- **Limit profiles**, **Global Upload Cap**, **Ping Monitor**, **Connection Speed**, the **Scenario editor**, and the **speed test** all live here — everything network-control in one place.

**Settings** (opened from the header gear)
Two tabs — *General · Appearance*:

- *General* — units, global hotkey, startup & window behavior (minimize on close, start minimized to tray, show/hide tray icon), settings backup (export/import), system cleanup, and a manual update check (one GitHub API call, only when you click it).
- *Appearance* — dark/light theme; **per-domain accent color** (Network / Hardware, full color palette for each); separate color pickers for download/upload graph, CPU, GPU, and RAM; **temperature color bands** (set the warm/hot thresholds and a flat color per band — cool/warm/hot, with a small eased transition at each boundary); ping good/bad gradient pickers.

---

## How the global upload cap works

The cap runs two enforcement layers simultaneously:

1. **QoS Group Policy (soft cap)** — written to `HKLM\SOFTWARE\Policies\Microsoft\Windows\QoS`. Applied instantly to new socket connections with zero CPU overhead once set.

2. **Firewall pulse enforcement (hard cap)** — every 100 ms Bansa measures actual bytes sent across all processes, runs a token-bucket calculation, and if the total is over budget it adds outbound firewall block rules for each app that's actively uploading. Rules are removed as soon as the budget recovers. This layer catches what QoS cannot: existing connections that were open before the cap was set, UDP traffic (games, video calls), and any timing gaps during policy propagation.

---

## How download limits work (and what they can't do)

Windows QoS Policy only throttles **outbound** traffic — there's no clean user-mode way to rate-limit inbound traffic per process.

Bansa's download limit works by:
1. Watching the app's actual download rate via ETW (the same monitor that powers the UI).
2. When the rate exceeds the cap, Bansa temporarily adds an inbound firewall block for that app.
3. When the rate drops back, the block is removed.

This results in a **stutter pattern**: bursts of download alternating with blocks. The block length scales with how far over the cap the app is — a download running far above the limit can be blocked for several seconds at a time before a short burst, while one hovering near the cap toggles quickly. Either way the **average rate** matches your cap, but the connection isn't smoothly paced like with a kernel-level driver. For most use cases (keeping a backup app from saturating your link, capping a video stream's bitrate) it works well enough.

---

## Design principles

1. No kernel drivers for anything network — monitoring, limits, and blocks use ETW, Defender Firewall, and QoS Policy only. No installer. No background service.
2. Single executable. Asks for admin elevation when launched.
3. Every change is tagged `Bansa-…` so cleanup finds and removes only Bansa's own changes.
4. Built entirely on Defender Firewall, QoS Policy, ETW, IP Helper API — all native Windows.

### The one exception: the hardware sensor driver

CPU/GPU temperatures can't be read from user mode on Windows. Bansa uses LibreHardwareMonitor (the same library behind FanControl and similar tools), which loads a small kernel driver (`Bansa.sys`, based on WinRing0) at runtime to read MSR/SMBus sensors. It exists only while Bansa runs: created at startup, unloaded and deleted on exit, with a safety-net delete in case a crash leaves the file behind. Nothing is installed and nothing persists across sessions. Everything network-related works without it.

See `design.md` for architecture and `REVERSIBILITY.md` for the full audit of system mutations.

---

## Full uninstall (no trace)

1. Open Bansa → **Settings → General → System changes → Clean up**.
2. Close Bansa.
3. Delete the `Bansa\` folder and `%LocalAppData%\Bansa\`.

Or run `Uninstall-Bansa.ps1` from an elevated PowerShell prompt for the same effect without launching the app.

Verify with `Inspect-Bansa.ps1` at any time to see what Bansa currently has on the system.

---

## Build from source

### Prerequisites

1. [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
2. Run as Administrator (required for ETW + firewall access during development).

### Run

```powershell
cd path\to\repo\src
dotnet run --project Bansa -c Release
```

### Publish (framework-dependent single exe)

```powershell
cd path\to\repo
.\pack-release.ps1
```

Output: `release\Bansa.zip` — extract to get `Bansa\Bansa.exe`.

---

## Project layout

```
Bansa\                         ← repo root
├── README.md
├── design.md
├── REVERSIBILITY.md
├── pack-release.ps1           ← builds Bansa.zip for distribution
├── Uninstall-Bansa.ps1
├── Inspect-Bansa.ps1
└── src\
    ├── Bansa.sln
    └── Bansa\
        ├── Bansa.csproj
        ├── app.manifest
        ├── App.xaml(.cs)
        ├── MainWindow.xaml(.cs)
        ├── Resources\
        ├── Themes\            ← Dark.xaml, Light.xaml, Controls.xaml
        ├── Views\             ← FloatingGraphWindow, HistoryView, SpeedTestView, …
        ├── Models\
        ├── Services\          ← NetworkMonitor, DownloadThrottler, QosManager, …
        └── ViewModels\
```

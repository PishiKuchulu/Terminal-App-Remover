# 💡 Terminal App Remover — Feature & Capability Notes

> **"All your Windows CLI package managers under one graceful, modern command center."**

---

## 🎯 The Core Problem & Our Creative Solution

| The Old Way (CLI Hassle) 😫 | The Terminal App Remover Way (Sleek GUI) 😎 |
|:---|:---|
| Remembering 4 different command syntaxes (`winget uninstall`, `choco uninstall -y`, `scoop uninstall`, `dotnet tool uninstall -g`) | **One Click to Uninstall** — Universal button with smart command generation. |
| Having no idea how much disk space your terminal apps consume | **Smart Size Engine** — Deep registry correlation calculates disk footprints in real-time. |
| CLI commands getting stuck indefinitely on network or CDN timeout | **Fail-Safe Process Sandbox** — per-manager scan watchdogs (60–120s) and a 10-minute uninstall watchdog, both with automatic tree killing and a Cancel button. Failures are reported under the list, never silent. |
| Forgetting whether an app was installed via Scoop, Choco, or WinGet | **Unified Global Search** — Instant keystroke filtering across Name, ID, and Source. |
| Opening CMD/PowerShell with elevated admin privileges manually | **Adaptive UAC Escalator** — Self-elevates for Choco while keeping Scoop in user-space. |

---

## 🚀 Deep-Dive Feature Breakdown

### 1. 🔄 Multi-Engine Parallel Scanner
- **Parallel Dispatch:** Launches concurrent asynchronous scanners for `WinGet`, `Chocolatey`, `Scoop`, `npm`, `pnpm`, `Python (pip)`, `Rust (Cargo)`, `Go`, and `.NET Tools` using `Task.WhenAll`.
- **Zero Freeze:** Scanning and size calculation run in the background, so the UI stays responsive while external managers are queried.
- **Honest Failures:** Each scanner reports its own outcome. Timeouts and non-zero exits become visible warnings under the list (with the manager's first error line) instead of silently contributing zero packages.
- **Dynamic Progress Bar:** An integrated, smooth `ProgressBar` signals background activity and automatically hides when complete.

---

### 2. 🧠 Smart Registry Footprint Engine (`InstalledSizeService`)
Terminal package managers typically don't report installed size directly in their list output. **Terminal App Remover** solves this with an intelligent heuristic scanner:
- **Bi-Hive & Bi-Architecture Traversal:** Simultaneously scans:
  - `HKEY_LOCAL_MACHINE` (64-bit & 32-bit `Software\Microsoft\Windows\CurrentVersion\Uninstall`)
  - `HKEY_CURRENT_USER` (64-bit & 32-bit user installations)
- **Folder-First Measurement:** Real install folders are measured before the registry is consulted (npm/pnpm module trees, pip site-packages via PEP 376 `RECORD`, Scoop `apps\<app>\current`, Chocolatey `lib\<id>`, the .NET tool `.store`, Go/Cargo binaries) — the registry only fills gaps for WinGet/Chocolatey apps, so a chance name collision can no longer overwrite an exact measurement.
- **Best-Match Scoring Algorithm:** Every registry entry is scored against every package and the best score wins:
  1. *Exact String Match:* Compares sanitized display names (highest score).
  2. *Ellipsis & Truncation Recovery:* Detects and matches names clipped by WinGet (`…` and `...`).
  3. *Hierarchical ID Dissection:* Dissects vendor namespaces (e.g. `VideoLAN.VLC` $\rightarrow$ `VLC`).
- **Safe Version Compatibility:** Employs boundary-aware version prefix matching to avoid false positives (v1.1 ≠ v1.10, v1.0 ≠ v11.0), and a same-name entry with an incompatible version is rejected outright.
- **pnpm note:** pnpm links files from a shared content-addressable store, so a pnpm package's size is its directory's logical size; store files shared between packages are counted per package.
- **Human-Readable Formatter:** Displays sizes cleanly as `B`, `KB`, `MB`, or `GB` with a live tally in the status bar.

---

### 3. 🛡️ Adaptive Privilege & Execution Management
Windows package managers have fundamentally different permission architectures:
- **Chocolatey:** System-wide packages $\rightarrow$ Auto-elevated via `Verb = "runas"` (UAC prompt).
- **Scoop:** Per-user applications $\rightarrow$ Strictly sandboxed in user-mode via `cmd.exe /c` (prevents polluting Admin user profiles).
- **WinGet:** Handles its own elevation when touching system-level MSI/EXE installers.
- **Structured Execution:** Uninstalls run from a `(FileName, ArgumentList)` spec — never by building a command string and re-parsing it — so names containing spaces or shell metacharacters cannot corrupt the invocation. Go "uninstalls" delete the binary in-process and verify it is actually gone afterwards.
- **Exit Code Handling:** Recognizes `0`, `3010` (`ERROR_SUCCESS_REBOOT_REQUIRED`), and `1641` (reboot initiated) as success, with a clear user notice instead of reporting a failure. On failure the exit code and the tool's last error line are shown.

---

### 4. 🧯 Hang Prevention & Process Tree Watchdog
- **Stall Protection (scan):** If a package manager freezes waiting for network handshakes (e.g. geo-blocked CDNs, proxy timeouts, or interactive prompts), a per-manager watchdog fires — 120s for WinGet, 60s for the others — and the timeout is reported as a visible warning.
- **Stall Protection (uninstall):** Uninstalls run with captured output (except the elevated Chocolatey path, where Windows forbids redirection), a 10-minute watchdog, and a Cancel button — the UI never locks up forever behind a stuck installer.
- **Total Tree Annihilation:** Calls `process.Kill(entireProcessTree: true)` so zero orphan processes linger in task manager consuming CPU or RAM.

---

### 5. 🎨 Modern Windows Design & Ergonomics
- **WinUI 3 & Windows App SDK 2.3+:** Native Fluent Design with a Mica system backdrop and system theme support.
- **Per-Monitor V2 High-DPI Awareness:** Crisp rendering on 4K, ultrawide, and multi-monitor setups with mismatched scaling; the initial window size is scaled to the display's DPI and a minimum window size is enforced.
- **Dialog Concurrency Guard:** A guard flag makes extra clicks no-ops while a ContentDialog is open, preventing WinUI dialog re-entrancy crashes.
- **Manifest:** Embedded manifest requests `asInvoker` (the app itself never needs elevation; only the Chocolatey uninstall it launches does) and declares long-path awareness so deep `node_modules` trees can be measured.

---

## 📊 Summary of Capabilities

```
Terminal App Remover
 ├── 📥 Detection Layer (9 Parallel Engines)
 │    ├── WinGet CLI parser (Fixed-width table & whitespace fallback)
 │    ├── Chocolatey delimited stream reader
 │    ├── Scoop shim executor (via cmd.exe)
 │    ├── npm global JSON reader & node_modules inspector
 │    ├── pnpm global JSON reader & store analyzer
 │    ├── Python (pip) JSON parser & PEP 376 dist-info RECORD size engine
 │    ├── Rust (Cargo) .crates2.json & binary inspector
 │    ├── Go binary detector (go/bin)
 │    └── .NET global tools table parser
 ├── 💾 Analytical Layer
 │    ├── Multi-hive Registry reader (HKLM + HKCU, 64-bit & 32-bit)
 │    ├── Python site-packages & PEP 376 RECORD CSV file byte parser
 │    ├── npm & pnpm global node_modules recursive directory analyzer
 │    ├── Go and Cargo binary byte size analyzer
 │    └── Smart size heuristic matcher
 ├── 🖥️ Presentation Layer (WinUI 3)
 │    ├── Instant search & multi-column filtering
 │    ├── Asynchronous indeterminate loading indicator
 │    └── Interactive confirmation & uninstallation modals
 └── 🚀 Execution Layer
      ├── Watchdog timeouts (scan 60–120s, uninstall 10 min, cancellable) & tree cleanup
      ├── Elevation controller (runas / user-mode)
      ├── Structured (FileName, ArgumentList) process launches — no string re-parsing
      └── Exit code status interpreter (0, 3010 & 1641) with captured error output
```

---

<div align="center">
  <sub>Crafted with passion for a cleaner, faster Windows environment.</sub>
</div>

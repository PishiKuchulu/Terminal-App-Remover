# 💡 Terminal App Remover — Feature & Capability Notes

> **"All your Windows CLI package managers under one graceful, modern command center."**

---

## 🎯 The Core Problem & Our Creative Solution

| The Old Way (CLI Hassle) 😫 | The Terminal App Remover Way (Sleek GUI) 😎 |
|:---|:---|
| Remembering 4 different command syntaxes (`winget uninstall`, `choco uninstall -y`, `scoop uninstall`, `dotnet tool uninstall -g`) | **One Click to Uninstall** — Universal button with smart command generation. |
| Having no idea how much disk space your terminal apps consume | **Smart Size Engine** — Deep registry correlation calculates disk footprints in real-time. |
| CLI commands getting stuck indefinitely on network or CDN timeout | **Fail-Safe Process Sandbox** — 30-second watchdog timer with automatic tree killing. |
| Forgetting whether an app was installed via Scoop, Choco, or WinGet | **Unified Global Search** — Instant keystroke filtering across Name, ID, and Source. |
| Opening CMD/PowerShell with elevated admin privileges manually | **Adaptive UAC Escalator** — Self-elevates for Choco while keeping Scoop in user-space. |

---

## 🚀 Deep-Dive Feature Breakdown

### 1. 🔄 Multi-Engine Parallel Scanner
- **Parallel Dispatch:** Launches concurrent asynchronous scanners for `WinGet`, `Chocolatey`, `Scoop`, `npm`, `pnpm`, `Python (pip)`, `Rust (Cargo)`, `Go`, and `.NET Tools` using `Task.WhenAll`.
- **Zero Freeze:** The UI remains silky-smooth at 60+ FPS while background threads query external managers.
- **Dynamic Progress Bar:** An integrated, smooth `ProgressBar` signals background activity and automatically hides when complete.

---

### 2. 🧠 Smart Registry Footprint Engine (`InstalledSizeService`)
Terminal package managers typically don't report installed size directly in their list output. **Terminal App Remover** solves this with an intelligent heuristic scanner:
- **Bi-Hive & Bi-Architecture Traversal:** Simultaneously scans:
  - `HKEY_LOCAL_MACHINE` (64-bit & 32-bit `Software\Microsoft\Windows\CurrentVersion\Uninstall`)
  - `HKEY_CURRENT_USER` (64-bit & 32-bit user installations)
- **3-Tier Matching Algorithm:**
  1. *Exact String Match:* Compares sanitized display names.
  2. *Ellipsis & Truncation Recovery:* Detects and matches names clipped by WinGet (`…` and `...`).
  3. *Hierarchical ID Dissection:* Dissects vendor namespaces (e.g. `VideoLAN.VLC` $\rightarrow$ `VLC`).
- **Safe Version Compatibility:** Employs boundary-aware version prefix matching to avoid false positives (e.g., preventing v1.0 from accidentally claiming v11.0's footprint).
- **Human-Readable Formatter:** Displays sizes cleanly as `B`, `KB`, `MB`, or `GB` with a live tally in the status bar.

---

### 3. 🛡️ Adaptive Privilege & Execution Management
Windows package managers have fundamentally different permission architectures:
- **Chocolatey:** System-wide packages $\rightarrow$ Auto-elevated via `Verb = "runas"` (UAC prompt).
- **Scoop:** Per-user applications $\rightarrow$ Strictly sandboxed in user-mode via `cmd.exe /c` (prevents polluting Admin user profiles).
- **WinGet:** Handles its own elevation when touching system-level MSI/EXE installers.
- **Exit Code 3010 Handling:** Recognizes `ERROR_SUCCESS_REBOOT_REQUIRED` as successful uninstallation with a clear user notice instead of reporting a failure.

---

### 4. 🧯 Hang Prevention & Process Tree Watchdog
- **Stall Protection:** If a package manager freezes waiting for network handshakes (e.g. geo-blocked CDNs, proxy timeouts, or interactive prompts), a linked `CancellationTokenSource` triggers after 30 seconds.
- **Total Tree Annihilation:** Calls `process.Kill(entireProcessTree: true)` so zero orphan processes linger in task manager consuming CPU or RAM.

---

### 5. 🎨 Modern Windows Design & Ergonomics
- **WinUI 3 & Windows App SDK 2.3+:** Native Fluent Design with support for mica, acrylic strokes, and system themes.
- **Per-Monitor V2 High-DPI Awareness:** Crisp rendering on 4K, ultrawide, and multi-monitor setups with mismatched scaling.
- **Dialog Concurrency Guard:** Prevents WinUI runtime crashes by queuing and locking ContentDialog interactions.
- **Installer Heuristic Bypass:** Embedded manifest with `asInvoker` stops Windows UAC from misclassifying the app as a legacy installer.

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
      ├── Watchdog timeout & tree cleanup
      ├── Elevation controller (runas / user-mode)
      └── Exit code status interpreter (0 & 3010)
```

---

<div align="center">
  <sub>Crafted with passion for a cleaner, faster Windows environment.</sub>
</div>

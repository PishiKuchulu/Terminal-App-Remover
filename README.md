<div align="center">

# ⚡ Terminal App Remover

### Modern, unified GUI to scan and remove packages installed via CLI package managers on Windows.

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Windows App SDK](https://img.shields.io/badge/WinUI-3.0-0078D4?logo=windows&logoColor=white)](https://learn.microsoft.com/windows/apps/winui/)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-blue)](https://microsoft.com/windows)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![Build and Release](https://github.com/PishiKuchulu/Terminal-App-Remover/actions/workflows/build.yml/badge.svg)](https://github.com/PishiKuchulu/Terminal-App-Remover/actions/workflows/build.yml)

<p align="center">
  <b>Tired of remembering different uninstall syntax across WinGet, Chocolatey, Scoop, and .NET Tools?</b><br/>
  Terminal App Remover brings them all together into one sleek, responsive desktop interface.
</p>

</div>

---

## 🌟 Key Highlights

- 🔍 **Unified Multi-Manager Detection:** Concurrently scans **WinGet**, **Chocolatey**, **Scoop**, **npm**, **pnpm**, **Python (pip)**, **Rust (Cargo)**, **Go**, and **.NET Global Tools** in parallel. If a manager times out or fails, a warning under the list says so — packages never vanish silently.
- 💾 **Smart Disk Footprint Discovery:** Measures real install folders first (npm/pnpm modules, pip site-packages, Scoop/Chocolatey/.NET tool stores, Go/Cargo binaries), then falls back to 32-bit and 64-bit Windows Registry hives (`HKLM` & `HKCU`) for WinGet/Chocolatey apps, with best-match scoring and boundary-aware version checks.
- 🛡️ **Intelligent Privilege Escalation:** Seamlessly executes uninstallation commands with the right privileges (handles UAC for Chocolatey, user-mode execution for Scoop, npm, pip, Cargo & Go, and self-elevation for WinGet). WinGet itself, the package managers, shared Visual C++ / Windows framework packages, and this app are protected and cannot be removed from the list.
- ⚡ **Non-Blocking & Timeout Resilient:** Scans (60–120s per manager) and uninstalls (10 minutes, with a Cancel button and captured error output) are all bounded, with automated process tree cleanup.
- 🚀 **Portable (Self-Contained):** Zero prerequisites — no .NET runtime to install. Download the single-file launcher (`TerminalAppRemover-SingleFile.exe`) and double-click it, or grab the ZIP of the app folder if you prefer.

---

## 📦 Supported Package Managers

| Package Manager | Scan Method | Size Detection | Elevation / UAC |
|:---|:---|:---:|:---:|
| 🪟 **WinGet** | `winget list` CLI parsing (Source column used; non-WinGet entries are labelled, system/framework packages protected) | Registry (best match) + Heuristics | Automatic Self-Elevation |
| 🍫 **Chocolatey** | `choco list --local-only --limit-output` | Install Folder + Registry | Automatic Admin UAC (`runas`) |
| 🍨 **Scoop** | `scoop list` (Batch/Shim) | Install Folder (`scoop/apps/<app>`) | User-Space Mode |
| 🟢 **npm** | `npm list -g --depth=0 --json` | Direct Folder Analysis (prefix / nvm / Volta / fnm aware) | User-Space Mode |
| 📦 **pnpm** | `pnpm list -g --depth=0 --json` | Direct Folder Analysis | User-Space Mode |
| 🐍 **Python (pip)** | `pip list --format=json` (with probed `python -m pip` / `py -m pip` fallbacks) | Site-Packages Analysis | User-Space Mode |
| 🦀 **Rust (Cargo)** | `$CARGO_HOME/.crates2.json` / CLI | Binary Size Analysis | User-Space Mode |
| 🐹 **Go** | `$GOBIN` / `$GOPATH/bin` Executable Detection | Binary Size Analysis | User-Space Mode (verified in-process delete) |
| 🔷 **.NET Global Tools** | `dotnet tool list --global` | Tool Store Folder (`.dotnet/tools/.store`) | User-Space Mode |

---

## 🛠️ Tech Stack & Architecture

- **Framework:** .NET 10 (`net10.0-windows10.0.19041.0`)
- **UI:** WinUI 3 (Windows App SDK 2.3+)
- **Architecture:** x64 Native, Self-Contained Desktop App
- **High-DPI Awareness:** `PerMonitorV2` High-DPI support
- **CI/CD:** Automated GitHub Actions build pipeline

---

## 🚀 Getting Started

### Download Pre-built Release:
Requires Windows 10 version 2004 (build 19041) or later, or Windows 11, on x64.
Check the [Releases](https://github.com/PishiKuchulu/Terminal-App-Remover/releases) tab or the [GitHub Actions Artifacts](https://github.com/PishiKuchulu/Terminal-App-Remover/actions). Two downloads are available:

- **`TerminalAppRemover-SingleFile.exe`** — one single file, just download and double-click. On first run it unpacks the app to `%LOCALAPPDATA%\TerminalAppRemover` and launches it; later runs start directly. (A WinUI 3 exe truly alone cannot run — its XAML resources and native DLLs must exist as real files — so the single file is a small native launcher with the whole app embedded.)
- **`TerminalAppRemover-win-x64.zip`** — the self-contained app folder as a ZIP. Extract the whole folder and double-click `TerminalAppRemover.exe` inside it — the exe needs the files next to it, so don't move it out of the folder on its own.

### Build from Source:

```powershell
# Clone the repository
git clone https://github.com/PishiKuchulu/Terminal-App-Remover.git
cd Terminal-App-Remover

# Build in Release mode
dotnet build -c Release

# Publish Standalone Self-Contained Package (folder + ZIP under .\release)
.\build\build-release.ps1

# Run the parser/matching unit tests (pure logic, no Windows required)
dotnet run --project tests/TerminalAppRemover.Tests -c Release
```

---

## 📖 Feature Walkthrough

For a detailed, deep-dive tour of every feature, algorithm, and capability, read [FEATURES.md](FEATURES.md).

---

## 📄 License
This project is open-source under the [MIT License](LICENSE).

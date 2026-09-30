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

- 🔍 **Unified Multi-Manager Detection:** Concurrently scans **WinGet**, **Chocolatey**, **Scoop**, **npm**, **pnpm**, **Python (pip)**, **Rust (Cargo)**, **Go**, and **.NET Global Tools** in parallel.
- 💾 **Smart Disk Footprint Discovery:** Correlates terminal packages with 32-bit and 64-bit Windows Registry hives (`HKLM` & `HKCU`), npm/pnpm modules, and binary directories to calculate installed size.
- 🛡️ **Intelligent Privilege Escalation:** Seamlessly executes uninstallation commands with the right privileges (handles UAC for Chocolatey, user-mode execution for Scoop, npm, pip, Cargo & Go, and self-elevation for WinGet).
- ⚡ **Non-Blocking & Timeout Resilient:** Protected against network hangs (e.g. CDN blocks or timeouts) with automated process tree cleanup.
- 🚀 **100% Portable (Self-Contained Single File):** Zero prerequisites. Just one standalone `.exe`.

---

## 📦 Supported Package Managers

| Package Manager | Scan Method | Size Detection | Elevation / UAC |
|:---|:---|:---:|:---:|
| 🪟 **WinGet** | `winget list` CLI parsing | Registry + Heuristics | Automatic Self-Elevation |
| 🍫 **Chocolatey** | `choco list --limit-output` | Registry Matching | Automatic Admin UAC (`runas`) |
| 🍨 **Scoop** | `scoop list` (Batch/Shim) | Registry / Fallback | User-Space Mode |
| 🟢 **npm** | `npm list -g --depth=0 --json` | Direct Folder Analysis | User-Space Mode |
| 📦 **pnpm** | `pnpm list -g --depth=0 --json` | Direct Folder Analysis | User-Space Mode |
| 🐍 **Python (pip)** | `pip list --format=json` | Site-Packages Analysis | User-Space Mode |
| 🦀 **Rust (Cargo)** | `~/.cargo/.crates2.json` / CLI | Binary Size Analysis | User-Space Mode |
| 🐹 **Go** | `go/bin` Executable Detection | Binary Size Analysis | User-Space Mode |
| 🔷 **.NET Global Tools** | `dotnet tool list --global` | Metadata Matching | User-Space Mode |

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
Check the [Releases](https://github.com/PishiKuchulu/Terminal-App-Remover/releases) tab or the [GitHub Actions Artifacts](https://github.com/PishiKuchulu/Terminal-App-Remover/actions) to download the latest `TerminalAppRemover-win-x64.zip`. Extract and double-click `TerminalAppRemover.exe`.

### Build from Source:

```powershell
# Clone the repository
git clone https://github.com/PishiKuchulu/Terminal-App-Remover.git
cd Terminal-App-Remover

# Build in Release mode
dotnet build -c Release

# Publish Standalone Self-Contained Package
.\build\build-release.ps1
```

---

## 📖 Feature Walkthrough

For a detailed, deep-dive tour of every feature, algorithm, and capability, read [FEATURES.md](FEATURES.md).

---

## 📄 License
This project is open-source under the [MIT License](LICENSE).

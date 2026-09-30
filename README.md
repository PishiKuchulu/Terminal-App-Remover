# Terminal App Remover

Terminal App Remover scans and removes applications installed via Windows package managers (WinGet, Chocolatey, Scoop, and .NET Global Tools).

## Build and Run

### Build Solution:
```powershell
dotnet build -c Release
```

### Publish Self-Contained Release (win-x64):
```powershell
.\build\build-release.ps1
```
Or:
```powershell
dotnet publish src/TerminalAppRemover/TerminalAppRemover.csproj -c Release -r win-x64 --self-contained true -o release/TerminalAppRemover-win-x64
```


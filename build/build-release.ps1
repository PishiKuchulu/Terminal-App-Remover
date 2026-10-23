$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$project = Join-Path $root 'src\TerminalAppRemover\TerminalAppRemover.csproj'
$publishDir = Join-Path $root 'release\TerminalAppRemover-win-x64'
if (Test-Path $publishDir) {
    Remove-Item -Path $publishDir -Recurse -Force
}
& dotnet publish $project -c Release -r win-x64 --self-contained true -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

# The self-contained folder as a ZIP.
$zipPath = Join-Path $root 'release\TerminalAppRemover-win-x64.zip'
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath

# The single-file download: a NativeAOT bootstrapper with the app ZIP embedded
# (a WinUI 3 exe alone cannot run — its XAML resources and native DLLs must
# sit next to it; the bootstrapper extracts them to %LOCALAPPDATA% on first run).
$bootstrapper = Join-Path $root 'bootstrapper\TerminalAppRemover.Bootstrapper.csproj'
$singleDir = Join-Path $root 'release\singlefile'
if (Test-Path $singleDir) { Remove-Item -Path $singleDir -Recurse -Force }
& dotnet publish $bootstrapper -c Release -r win-x64 -p:PayloadZip="$zipPath" -o $singleDir
if ($LASTEXITCODE -ne 0) { throw "bootstrapper publish failed with exit code $LASTEXITCODE" }
$singleExe = Join-Path $root 'release\TerminalAppRemover-SingleFile.exe'
Copy-Item (Join-Path $singleDir 'TerminalAppRemoverBootstrapper.exe') $singleExe -Force

Write-Host "Published: $publishDir"
Write-Host "ZIP:       $zipPath"
Write-Host "SingleFile: $singleExe"

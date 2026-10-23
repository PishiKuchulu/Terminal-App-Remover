$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$project = Join-Path $root 'src\TerminalAppRemover\TerminalAppRemover.csproj'
$publishDir = Join-Path $root 'release\TerminalAppRemover-win-x64'
if (Test-Path $publishDir) {
    Remove-Item -Path $publishDir -Recurse -Force
}
& dotnet publish $project -c Release -r win-x64 --self-contained true -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

# The supported distribution shape is the whole self-contained folder as a ZIP
# (WinUI 3 unpackaged apps cannot run as a single standalone exe).
$zipPath = Join-Path $root 'release\TerminalAppRemover-win-x64.zip'
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath
Write-Host "Published: $publishDir"
Write-Host "ZIP:       $zipPath"

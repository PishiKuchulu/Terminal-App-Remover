$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$project = Join-Path $root 'src\TerminalAppRemover\TerminalAppRemover.csproj'
$publishDir = Join-Path $root 'release\TerminalAppRemover-win-x64'
if (Test-Path $publishDir) {
    Remove-Item -Path $publishDir -Recurse -Force
}
& dotnet publish $project -c Release -r win-x64 --self-contained true -o $publishDir

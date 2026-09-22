# Publishes a self-contained Auto Clicker build into dist\<runtime>\.
# Examples:
#   .\build.ps1                       # for this machine (win-x64)
#   .\build.ps1 -Runtime linux-x64    # cross-compile for Linux
#   .\build.ps1 -Icons                # regenerate the icon files first
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')]
    [string]$Runtime = 'win-x64',
    [switch]$Icons
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if ($Icons) { & powershell -ExecutionPolicy Bypass -File .\tools\make-icons.ps1 }

dotnet publish src/AutoClicker/AutoClicker.csproj `
    --configuration Release `
    --runtime $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    --output "dist/$Runtime"
if ($LASTEXITCODE -ne 0) { throw "Build failed (dotnet exit code $LASTEXITCODE)." }

Write-Host "Built $(Resolve-Path "dist/$Runtime")"

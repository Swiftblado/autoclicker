# Builds dist\AutoClicker.exe using the C# compiler that ships with Windows (.NET Framework 4.x).
# No SDK or extra installs needed.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw '.NET Framework 4.x C# compiler not found.' }

if (-not (Test-Path 'src\app.ico')) { & .\tools\make-icon.ps1 }
New-Item -ItemType Directory -Force dist | Out-Null

$sources = Get-ChildItem src\*.cs | ForEach-Object { $_.FullName }
& $csc /nologo /target:winexe /optimize+ /platform:anycpu `
    /out:dist\AutoClicker.exe `
    /win32icon:src\app.ico `
    /win32manifest:src\app.manifest `
    /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
    $sources
if ($LASTEXITCODE -ne 0) { throw "Build failed (csc exit code $LASTEXITCODE)." }

Write-Host "Built $(Resolve-Path dist\AutoClicker.exe)"

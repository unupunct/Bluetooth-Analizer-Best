# Bluetooth Fleet Monitor — prepare a new development machine.
# Run from anywhere:  powershell -ExecutionPolicy Bypass -File .\Scripts\Setup-DevEnvironment.ps1
# Installs: .NET 10 SDK, Microsoft OpenJDK 17, Android command-line tools + SDK 35.
# Safe to re-run; already-installed components are skipped.

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Install-WingetPackage([string]$Id) {
    $listed = winget list --id $Id --accept-source-agreements 2>$null | Out-String
    if ($listed -match [regex]::Escape($Id)) { Write-Host "[ok] $Id already installed"; return }
    Write-Host "[..] Installing $Id"
    winget install --id $Id --accept-source-agreements --accept-package-agreements --silent --disable-interactivity
    if ($LASTEXITCODE -ne 0) { throw "winget install $Id failed ($LASTEXITCODE)" }
}

Install-WingetPackage 'Microsoft.DotNet.SDK.10'
Install-WingetPackage 'Microsoft.OpenJDK.17'

$jdk = (Get-ChildItem 'C:\Program Files\Microsoft' -Directory -Filter 'jdk-17*' | Sort-Object Name -Descending | Select-Object -First 1).FullName
if (-not $jdk) { throw 'JDK 17 not found under C:\Program Files\Microsoft' }
[Environment]::SetEnvironmentVariable('JAVA_HOME', $jdk, 'User'); $env:JAVA_HOME = $jdk
Write-Host "[ok] JAVA_HOME = $jdk"

$sdk = Join-Path $env:LOCALAPPDATA 'Android\Sdk'
$sdkManager = Join-Path $sdk 'cmdline-tools\latest\bin\sdkmanager.bat'
if (-not (Test-Path $sdkManager)) {
    Write-Host '[..] Downloading Android command-line tools'
    $tmp = Join-Path $env:TEMP 'bfm-cmdtools'
    New-Item -ItemType Directory -Force $tmp | Out-Null
    $zip = Join-Path $tmp 'cmdtools.zip'
    Invoke-WebRequest 'https://dl.google.com/android/repository/commandlinetools-win-11076708_latest.zip' -OutFile $zip
    Expand-Archive $zip -DestinationPath $tmp -Force
    New-Item -ItemType Directory -Force (Join-Path $sdk 'cmdline-tools') | Out-Null
    Move-Item (Join-Path $tmp 'cmdline-tools') (Join-Path $sdk 'cmdline-tools\latest')
    Remove-Item -Recurse -Force $tmp -Confirm:$false
}
[Environment]::SetEnvironmentVariable('ANDROID_HOME', $sdk, 'User'); $env:ANDROID_HOME = $sdk
Write-Host "[ok] ANDROID_HOME = $sdk"

Write-Host '[..] Accepting Android SDK licenses'
$yes = (1..30 | ForEach-Object { 'y' }) -join "`n"
$yes | cmd /c "`"$sdkManager`" --licenses" | Out-Null
Write-Host '[..] Installing Android SDK packages'
cmd /c "`"$sdkManager`" platform-tools platforms;android-35 build-tools;35.0.0"
if ($LASTEXITCODE -ne 0) { throw 'sdkmanager failed' }

# Android Gradle needs local.properties with the machine-specific SDK path (not portable, regenerated here).
$androidProject = Join-Path $PSScriptRoot '..\Android\BluetoothFleetMonitor'
if (Test-Path $androidProject) {
    $escaped = $sdk.Replace('\', '\\').Replace(':', '\:')
    Set-Content (Join-Path $androidProject 'local.properties') "sdk.dir=$escaped" -Encoding ascii
    Write-Host '[ok] Android local.properties written'
}

Write-Host ''
Write-Host 'Done. Open a NEW terminal so PATH/JAVA_HOME/ANDROID_HOME changes take effect.'

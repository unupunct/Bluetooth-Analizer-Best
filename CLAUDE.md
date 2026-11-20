# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Bluetooth Fleet Monitor: a local-only (no cloud, telemetry, or ads) system for diagnosing unreliable Bluetooth/BLE devices across a 170+ device fleet (Lovense toys, LED controllers, keyboards/mice, Logitech USB receivers). It has two independent apps: a Windows WPF command center that is the authoritative database and hosts a LAN API, and an Android scanner that syncs to it over Wi-Fi.

- `de-facut.txt` is the full specification and the **source of truth**. A copy lives at `dist/Documentation/SPECIFICATION.txt`.
- `dist/TASKS.md` is the phase-by-phase checklist. Tick items only when they build, are tested, and are verified. A UI that merely exists does not count. 🔧 marks items that need real hardware.
- `dist/PROJECT_PLAN.md` holds the environment state and hardware probe results.
- The user writes in Romanian. `README.md` and `TASKS.md` are in Romanian; technical docs are in English.

## Layout rule: everything inside `dist/`

The whole project lives in `dist/` because the owner moves it between computers. Use relative paths only. Build outputs go in `dist/Builds/`. Machine-specific files (Android `local.properties`, `bin/`, `obj/`) must be regenerable. `dist/Scripts/Setup-DevEnvironment.ps1` installs the toolchain on a new PC: .NET 10 SDK, OpenJDK 17, and Android SDK 35 in `%LOCALAPPDATA%\Android\Sdk`.

## Commands (Windows solution)

Run these from `dist/Windows`:

```powershell
dotnet build                                   # whole solution
dotnet test                                    # all tests (xUnit)
dotnet test --filter "FullyQualifiedName~IdentityResolver"   # single test class/method
dotnet run --project Desktop                   # launch WPF app
```

Headless self-tests of the built exe (`Desktop/bin/Debug/net10.0-windows10.0.19041.0/BluetoothFleetMonitor.Desktop.exe`; details in `dist/TEST_PLAN.md`). Use these instead of screenshotting the desktop:

```powershell
.\BluetoothFleetMonitor.Desktop.exe --verify-ui --out <dir> --data <tmpdir>      # renders all pages off-screen from labelled TEST data, counts binding errors, exit 0 = pass
.\BluetoothFleetMonitor.Desktop.exe --scan-test --seconds 20 --out <file> --data <tmpdir>   # real Passive scan into a throw-away DB; exit 0 data, 2 heard nothing, 3 nothing stored
```

EF migrations use the repo-local tool: `dotnet ef migrations add <Name> --project Database --startup-project Database --output-dir Migrations` (from `dist/Windows`, after `dotnet tool restore`).

In the Bash tool, `dotnet` may not be on PATH in a fresh shell. Prefix with `export PATH="/c/Program Files/dotnet:$PATH"`.

## Windows architecture (`dist/Windows`)

Clean layering. Dependencies point inward to `Application` → `Domain`.

- **Domain**: entities matching the spec's SQLite schema (§23).
- **Application**: interfaces and use cases. The `IBleScanner` abstraction lives here so other scanner backends can be added later.
- **Database**: EF Core + SQLite (migrations, indexes, batch writes in transactions).
- **Bluetooth**: Windows `BluetoothLEAdvertisementWatcher` implementation.
- **UsbMonitoring**: PnP arrival, removal, and re-enumeration events.
- **Diagnostics**: fault engine (spec §24 algorithms A–E) and the health score.
- **Desktop**: WPF + MVVM (CommunityToolkit.Mvvm), Generic Host, Serilog, LiveCharts2. The ASP.NET Core LAN API will be hosted in-process (Phase 3).
- **Tests**: xUnit, referencing everything except Desktop and UsbMonitoring.

Build settings:
- Platform projects (Bluetooth, UsbMonitoring, Desktop, Tests) target `net10.0-windows10.0.19041.0` for WinRT BLE APIs. The others target `net10.0`.
- Microsoft.EntityFrameworkCore.* and Microsoft.Extensions.* are **pinned to 10.0.x**. The 11.x packages require .NET 11 and fail with NU1202.
- Shared build settings are in `Directory.Build.props`.

## Non-negotiable behavior rules

- **BLE scanning is Passive by default.** It listens only: no scan requests, no connect, pair, or GATT. Active scan mode is an explicit opt-in setting. This applies to throwaway probes too.
- GATT connection tests (Phase 5) run only on one user-selected device, after a warning. Never send Lovense or proprietary commands. Never disconnect existing connections or auto-pair.
- Absence from a scan is never reported as a confirmed disconnection. Periods without scanner coverage are not device failures. If the evidence is too thin, report "Insufficient data".
- No fabricated or simulated data in production views. Simulated data is allowed only in tests or an explicit, visibly labelled Demo Mode.
- Never silently drop observations. The bounded queue's overflow must be recorded as an ApplicationEvent.
- Device identity must never rely on MAC alone (many devices use random addresses). Never auto-merge devices on identical names.
- Observations keep three separate timestamps: observation time, upload time, and processing time (UTC). Stale Android uploads must not appear as new detections.
- Never mark hardware tests as passed without real hardware.

## Hardware notes (dev PC)

- The adapter in use is a TP-Link (Realtek, VID 2357 PID 0604).
- On Windows 10 the generic Microsoft `bth.inf` driver starts the BLE watcher but receives **zero** advertisements. The TP-Link `rtkfilter.inf` filter driver fixes this.
- Device Manager still shows "Generic Bluetooth Adapter" after the fix; that is expected.
- With the driver, a passive scan sees ~85 devices in 20 s, including `LVS-*` (Lovense) names. Passive mode yields fewer names than Active because some names arrive only in scan responses.

<!-- graphify-rules-start (managed by `graphify init`) -->
## Use Graphify before grep

This repository is indexed by Graphify: a code graph over its call, dependency, and test structure, exposed through a connected Graphify MCP server. Before reaching for grep or reading files, use the Graphify tools your MCP client lists (their exact names and descriptions are in the server's tool list) for what the graph knows and a text search does not:

- find where a symbol, function, or class is defined (instead of grepping for it)
- understand how something works, or where a behavior is handled
- find who calls a function, or what it calls
- see what a change affects (its blast radius) and which tests cover it
- map a file's dependencies and dependents

Fall back to grep or file reads only for what the graph does not model: literal string or comment matches, non-indexed files, or reading a file you have already located. If no Graphify tools are listed, check the MCP server connection.
<!-- graphify-rules-end -->

## Android (`dist/Android/BluetoothFleetMonitor`)

Kotlin + Compose + Hilt + Room, minSdk 29, targetSdk 35, package `com.unupunct.fleetmonitor`. Toolchain lives outside the repo: JDK 17 at `C:\Program Files\Microsoft\jdk-17*`, SDK at `%LOCALAPPDATA%\Android\Sdk` (set `JAVA_HOME` / `ANDROID_HOME`; `local.properties` is machine-specific, written with forward slashes, and gitignored). Use the Git Bash tool for Gradle:

```bash
cd dist/Android/BluetoothFleetMonitor
./gradlew --no-daemon :app:testDebugUnitTest      # JVM tests (pure logic)
./gradlew --no-daemon :app:assembleDebug          # APK in app/build/outputs/apk/debug
```

- Accepting SDK licenses is the user's decision. `yes | sdkmanager --licenses` works in Git Bash but accepts ALL pending licenses, so only do it with their OK and delete any license files you were not asked to accept.
- The emulator does not work on this kind of machine; runtime checks need a real phone over ADB (`adb devices` must show `device`, not `unauthorized`).
- Domain logic (`domain/`) is pure Kotlin and unit tested. `FingerprintTest` pins the same values as the C# `Fingerprint_matches_the_cross_platform_reference_vectors`; change one, change both.
- Never add ad, analytics or telemetry libraries. Phase 2 deliberately has no `INTERNET` permission; Phase 3 adds it for the local sync only.
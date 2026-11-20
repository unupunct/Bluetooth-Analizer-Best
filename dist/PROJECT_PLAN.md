# Bluetooth Fleet Monitor — Project Plan

Source of truth: `Documentation/SPECIFICATION.txt` (full specification, Parts 1–11).

## Environment (inspected 2026-11-20)

| Item | State |
|---|---|
| OS | Windows 10 Pro 10.0.19045 (spec targets Windows 11; WinRT BLE APIs used are available on Win10 1809+) |
| .NET SDK | 10.0.404 (installed via winget during inspection) |
| Visual Studio | Not installed — builds use `dotnet` CLI only |
| JDK | Microsoft OpenJDK 17.0.21 (installed); legacy Java 8 also on PATH |
| Android SDK | `%LOCALAPPDATA%\Android\Sdk` — cmdline-tools, platform-tools, platforms;android-35, build-tools;35.0.0 |
| Gradle | Not installed globally — Android project will ship a Gradle wrapper |
| Bluetooth adapter | ASUS USB-BT500 (VID 0B05 PID 190E) registered but not present. Connected instead: TP-Link (VID 2357 PID 0604, Realtek, likely UB500), radio On, LE + Central role supported. Driver = Microsoft generic `bth.inf` 10.0.19041.5848. **Hardware probe 2026-11-20:** `BluetoothLEAdvertisementWatcher` starts (Passive and Active) with no error but receives 0 advertisements in 10 s with the generic driver only. **After installing TP-Link driver** (`rtkfilter.inf` 1.9.1038.3023, installed as a Realtek filter driver — Device Manager still shows "Generic Bluetooth Adapter"/`bth.inf`, which is expected): Active scan 15 s → **91 distinct addresses, 949 advertisements**, RSSI −60…−82 dBm (Govee, YNLUX/YONGNUO LED, Sony ILCE-6100, SA_* devices, many unnamed/random addresses). Real Windows BLE scanning **verified on hardware**. |
| Disk | ~20 GB free on C: before installs — monitor Gradle cache growth |

## Phases

Each phase ends with: build, automated tests, update `IMPLEMENTATION_STATUS.md`, record hardware limitations.

### Phase 1 — Core Windows application
1. Solution skeleton (Domain, Application, Infrastructure/Database, Bluetooth, UsbMonitoring, Diagnostics, Server, Desktop, Tests).
2. SQLite + EF Core schema (all tables of spec §23) with migrations and indexes.
3. Device identity resolver (spec §6) — never MAC-only, no name-only merge.
4. Windows BLE scanner behind `IBleScanner` abstraction (`BluetoothLEAdvertisementWatcher`), real adapter state reporting.
5. Bounded observation pipeline (Channel) → batched DB writer.
6. WPF dark shell + navigation; Dashboard, Live Monitor, Device Inventory, Device History, Event Log.
7. Serilog structured logging.
**Deliverable:** Windows app that performs real BLE scanning. Requires the adapter to be present for hardware validation.

### Phase 2 — Android scanner
Kotlin/Compose/Hilt/Room project with Gradle wrapper; permissions (Android 10–14+, Nearby Devices on 12+); foreground scanning service; background filtered/PendingIntent scanning; inspection sessions; local Room store. **Deliverable:** installable debug APK.

### Phase 3 — Integration
ASP.NET Core API hosted inside the desktop app (Kestrel bound to selected LAN interface), QR pairing with per-scanner tokens, batch upload with idempotency keys, Android WorkManager upload queue with acknowledgements.

### Phase 4 — Fault detection
Algorithms A–E (presence gaps with coverage awareness, RSSI stability, correlation, repeatability, evidence-based recommendations), configurable health score, Worst Offenders screen.

### Phase 5 — Active diagnostics
User-selected single-device GATT connection tests with explicit warning dialog; no Lovense control; never disconnect existing connections.

### Phase 6 — Release
Performance, backup/restore, portable self-contained win-x64 publish, signed APK, user manual, hardware acceptance checklist.

## Working rules (owner decision 2026-11-20)
- **Default scanning mode is Passive** (listen-only, no scan requests, no connections). Active scanning is an explicit opt-in setting. Passive probe: 85 addresses / 981 adverts / 17 named in 20 s (incl. `LVS-Z14`, `LVS-Lush`); Active probe: 78 / 1172 / 36 named — Active recovers names sent only in scan responses.

## Working rules (from spec §A)
- No fake Bluetooth data; demo/simulated data only behind an explicit Demo Mode flag and visibly labelled.
- Absence from a scan ≠ disconnection.
- Never silently discard observations (bounded queue applies backpressure + logs/records overflow as an ApplicationEvent).
- Do not mark hardware tests as passed without hardware.

# Implementation Status

Last updated: 2026-11-20, end of Phase 1 work. Build: `dotnet build` in `dist/Windows` is clean (0 warnings, 0 errors). Tests: 67 passed, 0 failed.

## Completed and verified

| Area | What exists | How it was verified |
|---|---|---|
| Database | All 10 spec tables, indexes, `InitialCreate` migration, UTC timestamp handling, online backup, integrity check | 13 automated tests |
| Device identity | Stable-address match, rotating-address memory, fingerprint match only when unique, never name-only | 10 automated tests |
| Observation pipeline | Bounded queue, batched transactional writes, retries, overflow and write-failure recorded as events, graceful flush on stop | 6 automated tests |
| Store | Idempotent batches (duplicate id stored once), stale uploads cannot overwrite current state, three separate timestamps | 8 automated tests |
| Device admin | Edit details, CSV export/import (formula-safe), manual merge and split with audit events, bucketed history queries | 11 automated tests |
| Scanner health | Pure state machine: running / running-but-deaf (driver hint) / interrupted / adapter unavailable, coverage gaps; address-kind classifier | 12 automated tests |
| Presence status | Detected / Missing / Uncertain, coverage-aware | 7 automated tests |
| Windows UI | Dark shell, 14-section navigation, Dashboard, Live Monitor, Inventory, History (chart), Scanner, Event Log, Settings | `--verify-ui`: all 14 pages render with 0 binding errors, chart produces series (TEST data) |
| App lifecycle | Start-up (backup-before-migrate, migrate, integrity check), crash logging, graceful shutdown | Ran the real app: started, responded, exited with code 0, scanner event recorded in the database |
| No-adapter path | Scanner reports `AdapterUnavailable` and the Dashboard warns | Observed earlier on this PC while no adapter was present |
| Real BLE scan | Passive scan -> identity -> SQLite | `--scan-test`: 1,067 real advertisements, 56 devices, 0 lost (TP-Link UB500 V3.6 driver) |

## Implemented but NOT yet verified on real hardware

- `WindowsBleScanner`: Passive scanning and "deaf scanner" detection are now verified on real hardware (see radio tests below). Still unverified: Active mode, auto-restart after unplug/replug, Bluetooth off/on, 30+ minute stability.
- The live data path into the **GUI** (scanner -> pipeline -> database is verified by `--scan-test`; the Live Monitor screen showing real devices has not been looked at by a person yet).
- Continuous scanning for 30+ minutes, UI responsiveness under real advertisement rates (hundreds per second).
- Windows 10. Code targets `net10.0-windows10.0.19041.0` with minimum OS 10.0.17763.0 (guarded call for the address type, which needs build 19041). Only Windows 11 (this PC) has been run.

### Radio tests, 2026-11-20 (Windows 11 build 22631, TP-Link UB500, VID 2357 PID 0604)

1. **Before the vendor driver:** `--scan-test --seconds 30` found the adapter, started the watcher, then correctly reported `RunningNoAdvertisements` with the driver hint: **0 advertisements**. Cause: generic Microsoft `bth.inf` driver (BTHUSB only, no filter). This verified the "deaf scanner" detection on real hardware.
2. **After installing TP-Link UB500 V3.6 (driver 1.9.1051.3016, stack BTHUSB + RtkBtFilter):** `--scan-test --seconds 30` in Passive mode: **PASS**. 1,067 advertisements received and 1,067 stored, 0 queue overflow, 0 write errors, 56 distinct devices created (10 with a name, 1 `LVS-` Lovense). Address kinds seen: Public, RandomStatic, RandomResolvablePrivate and RandomNonResolvablePrivate. Worst observed-to-processed delay 0.72 s.
   - Identity heuristics in the wild: 3 devices absorbed more than one rotating address through a unique-fingerprint match (confidence 0.7). These are exactly the matches to review manually; the heuristic can be wrong when only one of two similar products is known.
   - Still untested: 30+ minute run, unplug/replug recovery, Bluetooth off/on, Active mode, Windows 10.
## Android (Phase 2) - built, not yet run on a phone

| Area | State | Verification |
|---|---|---|
| Project | Kotlin 2.0.21, Compose (Material 3), Hilt, Room 2.6.1, AGP 8.7.3, Gradle 8.10.2 wrapper (pinned checksum), minSdk 29, targetSdk 35 | `gradlew assembleDebug` succeeds |
| Domain logic | Windows-compatible device fingerprint, sampling-window aggregator, presence status, scanner health + coverage gaps, RSSI labels/trend/stability | 28 JVM unit tests pass; the fingerprint test pins values produced by the C# code |
| Scanning | `BluetoothLeScanner` engine with a bounded queue, per-address sampling windows, batched Room writes, retries, recorded overflow, honest status | compiles; **not run on a phone** |
| Modes | A Continuous (foreground service + notification), B Background (OS-managed PendingIntent scan, exact-name filters), C Inspection (locations, notes, sessions) | compiles; **not run on a phone** |
| Interruptions | Bluetooth off/on, scan failure with retry, revoked permission, process/phone restart (heartbeat gap recorded) | compiles; **not run on a phone** |
| Screens | Scanner, Nearby, Radar, Inspector, Signal History, Sessions, Sync Status (placeholder), Settings, plus Event log | compile; **never rendered** |
| APK | `dist/Builds/Android/FleetMonitor-0.2.0-debug.apk`, 24.8 MB, debug-signed | `aapt2 dump permissions`: BLUETOOTH_SCAN (neverForLocation), foreground-service, notifications, legacy BT/location capped at API 30. **No INTERNET permission.** No ad/analytics/telemetry libraries found in the package |

Design notes: a phone hears hundreds of advertisements per second, so each device gets one stored window per sampling interval (defaults: 10 s continuous, 2 s inspection, 30 s background) holding last/min/max/average RSSI and the number of advertisements heard. Every advertisement is counted; nothing is silently discarded. Rows that were never acknowledged by the server (Phase 3) are never deleted. Android does not reveal whether an address is public or random, so every distinct address is its own device on the phone and the Windows server reconciles identities in Phase 3.

Not verified: everything that needs a real phone (permission prompts, scan results, foreground service survival with the screen off, background mode, Bluetooth toggle recovery, battery behaviour, UI layout on a real screen). No phone was attached over ADB.
## Not built yet (by design)

Fault detection engine, health score, Worst Offenders, signal analysis (Phase 4); LAN API with QR pairing and sync (Phase 3); active connection tests (Phase 5); USB monitor, reports, in-app restore, release packaging (Phase 6); iOS (deferred; needs a Mac). Their navigation entries are marked with their phase and show "Not implemented yet". The Health column shows "-" instead of a number.

## Known gaps inside Phase 1

- Split has a service and tests but **no UI** (merge has a UI).
- Inventory category is free text (no preset list).
- The Dashboard "events" feed shows scanner and application events only. Presence events (device went missing / came back) are Phase 4, because a naive window rule would produce false alarms.
- Live Monitor has no persisted column layout.

## Build errors / outstanding bugs

None known. Bugs found and fixed during Phase 1: ComboBox showing type names, filters leaking between pages (shared collection view), `CollectionViewSource` NRE in DataGrid, async-dispose crash on exit, EF SQL logged at Info level, Dashboard property hiding the `ScannerState` enum.
